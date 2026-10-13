using System.Net;
using System.Text.Json.Serialization;

namespace RiotAccounts.Core;

// Why a run ended early: every later step would fail the same way, so the run stops instead of burning the rate limit.
[JsonConverter(typeof(JsonStringEnumConverter<RefreshStop>))]
public enum RefreshStop { None, Key, RateLimit, Network }

// One step (account x queue, Queue null = ranks only). Messages are user-facing and never contain keys or PUUIDs.
public sealed record RefreshOutcome(Guid AccountId, string Account, string? Queue, bool Ok, string Message, DateTimeOffset At);

public sealed record RefreshResult(DateTimeOffset StartedAt, DateTimeOffset CompletedAt, RefreshStop Stop, List<RefreshOutcome> Outcomes)
{
    [JsonIgnore] public int Failures => Outcomes.Count(o => !o.Ok);
}

// Every account or one, every queue or one, or only the ranks (one call per account).
public sealed record RefreshTarget(Guid? AccountId = null, string? Queue = null, int Count = RefreshCycle.DefaultCount, bool RanksOnly = false)
{
    [JsonIgnore] public bool IsFull => AccountId == null && Queue == null && !RanksOnly;
}

/// <summary>
/// Refreshes accounts one step at a time through a stats provider. A failing step is recorded and the run moves on; a rejected key,
/// an exhausted rate-limit retry or a lost connection stops the run.
/// </summary>
public sealed class RefreshCycle(Store store, IGameStatsProvider provider, Action<RiotAccount, string?, Exception>? unexpected = null)
{
    // The provider only fetches match details it has not saved yet, so a larger count costs extra calls on the first run only.
    public const int DefaultCount = 50;
    public static readonly IReadOnlyList<string> AllQueues = [Queues.Solo, Queues.Flex, Queues.Normal];

    public async Task<RefreshResult> RunAsync(RefreshTarget target, IProgress<string> progress, CancellationToken ct)
    {
        if (target.Queue != null) _ = Queues.Get(target.Queue);
        var started = DateTimeOffset.UtcNow;
        var outcomes = new List<RefreshOutcome>();
        var accounts = store.Accounts().Where(a => target.AccountId is not { } id || a.Id == id).ToList();
        if (target.AccountId != null && accounts.Count == 0) throw new ArgumentException("アカウントが見つかりません。");
        IReadOnlyList<string?> steps = target.RanksOnly ? [null] : target.Queue is { } only ? [only] : [.. AllQueues];
        for (var i = 0; i < accounts.Count; i++)
        {
            foreach (var queue in steps)
            {
                ct.ThrowIfCancellationRequested();
                // Earlier steps can take minutes: pick up edits and skip an account deleted meanwhile.
                var account = store.Accounts().SingleOrDefault(a => a.Id == accounts[i].Id);
                if (account == null) break;
                var prefix = $"{account.Label}（{i + 1}/{accounts.Count}）{(queue == null ? "ランク" : Queues.Get(queue).Name)}: ";
                var relay = new Relay(message => progress.Report(prefix + message));
                RefreshStop stop;
                string message;
                try
                {
                    if (queue == null) await provider.RefreshRanksAsync(account, ct, relay);
                    else await provider.RefreshAnalysisAsync(account, queue, target.Count, relay, ct);
                    outcomes.Add(new(account.Id, account.Label, queue, true, "更新しました。", DateTimeOffset.UtcNow));
                    continue;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (RiotApiException error) { stop = StopFor(error.StatusCode); message = error.Message; }
                catch (HttpRequestException) { stop = RefreshStop.Network; message = "Riot APIに接続できませんでした。"; }
                catch (TaskCanceledException) { stop = RefreshStop.Network; message = "Riot APIの応答がタイムアウトしました。"; }
                catch (Exception error)
                {
                    unexpected?.Invoke(account, queue, error);
                    stop = RefreshStop.None; message = $"更新に失敗しました（{error.GetType().Name}）。";
                }
                outcomes.Add(new(account.Id, account.Label, queue, false, message, DateTimeOffset.UtcNow));
                if (stop != RefreshStop.None) return new(started, DateTimeOffset.UtcNow, stop, outcomes);
            }
        }
        return new(started, DateTimeOffset.UtcNow, RefreshStop.None, outcomes);
    }

    private static RefreshStop StopFor(HttpStatusCode? status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => RefreshStop.Key,
        HttpStatusCode.TooManyRequests => RefreshStop.RateLimit,
        _ => RefreshStop.None
    };

    private sealed class Relay(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
}

/// <summary>
/// When the next automatic full run is due. It compares wall-clock times, so a run missed while the host slept starts soon after it wakes.
/// </summary>
public static class RefreshSchedule
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan RetryAfterInterruption = TimeSpan.FromMinutes(30);

    // A rejected key waits for the next regular run (or a new key); a rate limit or a lost connection is retried sooner.
    public static DateTimeOffset NextDue(RefreshResult? last, TimeSpan interval) => last switch
    {
        null => DateTimeOffset.MinValue,
        { Stop: RefreshStop.RateLimit or RefreshStop.Network } => Min(last.CompletedAt + RetryAfterInterruption, last.StartedAt + interval),
        _ => last.StartedAt + interval
    };

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;
}
