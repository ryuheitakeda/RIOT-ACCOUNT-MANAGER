using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using RiotAccounts.Core;

namespace RiotAccounts.Web;

/// <summary>
/// Runs every refresh one at a time: the scheduled full run and the jobs queued from the pages or the WPF app. The server is then the
/// only user of the Riot API key, so RiotApi's throttle sees every request made with it.
/// </summary>
public sealed class RefreshWorker(Store store, LolStatsProvider provider, RiotApi riot, ApiKey key, WebSettings settings, ILogger<RefreshWorker> logger)
    : BackgroundService
{
    private const string Kind = "web", LastRunId = "lastRun", RateLimitsId = "rateLimits";
    private const int KeptJobs = 50;
    // After a restart (deploy, reboot) an overdue run waits a little, so a crash loop cannot hammer the API.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    private sealed record Entry(WebJob View, RefreshTarget Target);
    private readonly Channel<Guid> queue = Channel.CreateUnbounded<Guid>(new() { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, Entry> jobs = new();
    private readonly object gate = new();
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private Guid? running;
    private volatile bool keyChanged;
    private DateTimeOffset? lastChangedAt;

    public static string Version { get; } =
        typeof(RefreshWorker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    public RefreshResult? LastRun => store.Read<RefreshResult>(Kind, LastRunId);
    // A rejected key waits for the next regular run unless a new key has been saved since.
    public DateTimeOffset NextDue => keyChanged && LastRun is { Stop: RefreshStop.Key } ? DateTimeOffset.MinValue : RefreshSchedule.NextDue(LastRun, settings.Interval);
    public WebJob? Current { get { lock (gate) return running is { } id && jobs.TryGetValue(id, out var entry) ? entry.View : null; } }
    public int QueuedCount => jobs.Values.Count(e => e.View.State == WebJob.Queued);
    public WebJob? Job(Guid id) => jobs.TryGetValue(id, out var entry) ? entry.View : null;
    public void KeyChanged() => keyChanged = true;
    // Moves when a job finished or an account was edited, so the WPF app can skip the snapshot while nothing changed.
    // Kept in memory only: after a restart clients see null and import once.
    public void MarkChanged() { lock (gate) lastChangedAt = DateTimeOffset.UtcNow; }
    private DateTimeOffset? LastChangedAt { get { lock (gate) return lastChangedAt; } }
    public WebStatus Status() => new(Version, key.Registered, Current, QueuedCount, LastRun, NextDue, settings.RefreshIntervalHours, RateLimits(), LastChangedAt);

    // The latest observation per host and route, kept across restarts.
    public List<RateLimitObservation> RateLimits() =>
        [.. (store.Read<List<RateLimitObservation>>(Kind, RateLimitsId) ?? []).Concat(riot.RateLimits)
            .GroupBy(o => (o.Host, o.Route)).Select(g => g.MaxBy(o => o.ObservedAt)!)
            .OrderBy(o => o.Host, StringComparer.Ordinal).ThenBy(o => o.Route, StringComparer.Ordinal)];

    // Whether a waiting or running job may still write this account's records; a full run touches every account.
    public bool Touches(Guid accountId) => jobs.Values.Any(e => !e.View.Finished && (e.Target.AccountId == null || e.Target.AccountId == accountId));

    // The same request twice (a double click, a WPF retry) joins the job that is already waiting or running.
    public WebJob Enqueue(RefreshTarget target)
    {
        lock (gate)
        {
            if (jobs.Values.FirstOrDefault(e => !e.View.Finished && e.Target == target) is { } existing) return existing.View;
            var view = Add(target);
            queue.Writer.TryWrite(view.Id);
            return view;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid? next = null;
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                wait.CancelAfter(Tick);
                try { next = await queue.Reader.ReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
            }
            next ??= Scheduled();
            if (next is { } id) await RunAsync(id, stoppingToken);
        }
    }

    // Wall-clock check once a minute, so a run missed while the Mac slept starts soon after it wakes.
    private Guid? Scheduled()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < startedAt + StartupDelay || now < NextDue || !key.Registered) return null;
        lock (gate)
        {
            keyChanged = false;
            return Add(new RefreshTarget(Count: settings.MatchCount)).Id;
        }
    }

    private WebJob Add(RefreshTarget target)
    {
        var view = new WebJob(Guid.NewGuid(), WebJob.Queued, null, null, target.AccountId, target.Queue, target.RanksOnly, DateTimeOffset.UtcNow, null, null);
        jobs[view.Id] = new(view, target);
        return view;
    }

    private void Update(Guid id, Func<WebJob, WebJob> change)
    {
        lock (gate) if (jobs.TryGetValue(id, out var entry)) jobs[id] = entry with { View = change(entry.View) };
    }

    private async Task RunAsync(Guid id, CancellationToken ct)
    {
        if (!jobs.TryGetValue(id, out var entry)) return;
        lock (gate) running = id;
        Update(id, j => j with { State = WebJob.Running, StartedAt = DateTimeOffset.UtcNow });
        var (state, message) = (WebJob.Failed, "");
        try
        {
            if (!key.Registered) message = "Riot APIキーが未登録です。設定画面で登録してください。";
            else
            {
                var cycle = new RefreshCycle(store, provider, (account, queue, error) =>
                    logger.LogError(error, "想定外のエラー（{Account} {Queue}）", account.Label, Format.QueueName(queue)));
                var result = await cycle.RunAsync(entry.Target, new Relay(progress => Update(id, j => j with { Progress = progress })), ct);
                if (entry.Target.IsFull) store.Write(Kind, LastRunId, result);
                (state, message) = (result.Stop == RefreshStop.None && result.Failures == 0 ? WebJob.Succeeded : WebJob.Failed, Summary(result, entry.Target.IsFull));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { message = "サーバーの停止で中断しました。"; }
        catch (ArgumentException error) { message = error.Message; }
        catch (Exception error)
        {
            logger.LogError(error, "更新に失敗しました");
            message = $"更新に失敗しました（{error.GetType().Name}）。";
        }
        try { store.Write(Kind, RateLimitsId, RateLimits()); }
        catch (Exception error) { logger.LogWarning(error, "レート制限の記録を保存できませんでした"); }
        lock (gate)
        {
            running = null;
            // A failed job may have saved some steps, so every finished job counts as a change.
            lastChangedAt = DateTimeOffset.UtcNow;
            Update(id, j => j with { State = state, Message = message, FinishedAt = DateTimeOffset.UtcNow });
            foreach (var old in jobs.Values.Where(e => e.View.Finished).OrderByDescending(e => e.View.FinishedAt).Skip(KeptJobs).ToList())
                jobs.TryRemove(old.View.Id, out _);
        }
        logger.LogInformation("更新終了（{Target}）: {Message}", Describe(entry.Target), message);
    }

    private static string Summary(RefreshResult result, bool full)
    {
        var retry = full ? "30分後に再試行します。" : "";
        var failed = result.Outcomes.Where(o => !o.Ok).Take(3).Select(o => $"{o.Account} {Format.QueueName(o.Queue)}: {o.Message}");
        return result.Stop switch
        {
            RefreshStop.Key => "Riot APIキーが拒否されたため中断しました。設定画面でキーを確認してください。",
            RefreshStop.RateLimit => $"取得制限が続いたため中断しました。{retry}",
            RefreshStop.Network => $"Riot APIに接続できないため中断しました。{retry}",
            _ when result.Failures > 0 => $"{result.Outcomes.Count - result.Failures}/{result.Outcomes.Count}件を更新しました。失敗: {string.Join(" / ", failed)}",
            _ => $"{result.Outcomes.Count}件を更新しました。"
        };
    }

    private string Describe(RefreshTarget target)
    {
        var account = target.AccountId is { } id ? store.Accounts().SingleOrDefault(a => a.Id == id)?.Label ?? "削除済み" : "全アカウント";
        return target.RanksOnly ? $"{account} ランク" : $"{account} {(target.Queue == null ? "全キュー" : Format.QueueName(target.Queue))}";
    }

    private sealed class Relay(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
}
