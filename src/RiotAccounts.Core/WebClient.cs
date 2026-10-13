using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace RiotAccounts.Core;

// A failure of the web server or of the connection to it. It is an InvalidOperationException, so the WPF app shows its message as is.
public sealed class WebSourceException(string message, HttpStatusCode? statusCode = null) : InvalidOperationException(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>Client for the web server's JSON API (docs/web-server.md). It never calls Riot; only the server does.</summary>
public sealed class WebStatsClient(HttpClient http, Func<string?> getBaseUrl)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<WebStatus> StatusAsync(CancellationToken ct) => Send<WebStatus>(HttpMethod.Get, "/api/status", null, ct);
    public Task<WebSnapshot> SnapshotAsync(CancellationToken ct) => Send<WebSnapshot>(HttpMethod.Get, "/api/snapshot", null, ct);
    public Task<WebAccount> AccountAsync(Guid id, CancellationToken ct) => Send<WebAccount>(HttpMethod.Get, $"/api/accounts/{id}", null, ct);
    public Task<WebJob> RefreshAsync(WebRefreshRequest request, CancellationToken ct) => Send<WebJob>(HttpMethod.Post, "/api/refresh", request, ct);
    public Task<WebJob> JobAsync(Guid id, CancellationToken ct) => Send<WebJob>(HttpMethod.Get, $"/api/jobs/{id}", null, ct);

    // Only the non-secret fields leave this PC; credentials stay in the local store.
    public Task<WebAccount> SaveAccountAsync(RiotAccount account, CancellationToken ct) => Send<WebAccount>(HttpMethod.Put, $"/api/accounts/{account.Id}",
        new WebAccountInput(account.Label, account.Lol.GameName, account.Lol.TagLine, account.Lol.Platform, account.Note), ct);

    public static Uri? BaseUri(string? value) =>
        Uri.TryCreate(value?.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.Query.Length == 0 ? uri : null;

    private async Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var root = BaseUri(getBaseUrl()) ?? throw new WebSourceException("設定でWeb版のURLを登録してください。");
        using var request = new HttpRequestMessage(method, new Uri(root.AbsoluteUri.TrimEnd('/') + path));
        if (body != null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException) { throw new WebSourceException("Web版に接続できません。URLと、このPCのTailscaleの接続を確認してください。"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new WebSourceException("Web版の応答がタイムアウトしました。"); }
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw new WebSourceException(await ErrorMessage(response, ct), response.StatusCode);
            try { return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new WebSourceException("Web版から空の応答が返りました。"); }
            catch (JsonException) { throw new WebSourceException("Web版の応答を読み取れません。Web版とこのアプリの版を確認してください。"); }
        }
    }

    private static async Task<string> ErrorMessage(HttpResponseMessage response, CancellationToken ct)
    {
        try { if (await response.Content.ReadFromJsonAsync<WebError>(Json, ct) is { Message.Length: > 0 } error) return error.Message; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        return $"Web版でエラーが発生しました（HTTP {(int)response.StatusCode}）。";
    }
}

public sealed record WebSyncResult(int Imported, List<string> Skipped)
{
    public string Message => Skipped.Count == 0
        ? $"Web版から{Imported}件のアカウントを同期しました。"
        : $"Web版から{Imported}件を同期しました。同期しなかったアカウント：{string.Join("、", Skipped)}";
}

/// <summary>
/// The WPF "Web" source. Refreshes run on the web server, the only caller of the Riot API, and their results are copied into the
/// local store, so every view keeps reading saved data as before. Accounts are matched by ID (shared since the DB was migrated)
/// and copied only while the Riot ID and server agree.
/// </summary>
public sealed class WebStatsProvider(Store store, WebStatsClient client, TimeSpan? pollInterval = null) : IGameStatsProvider
{
    private readonly TimeSpan poll = pollInterval ?? TimeSpan.FromSeconds(1);

    public Task RefreshRanksAsync(RiotAccount account, CancellationToken cancellationToken, IProgress<string>? progress = null) =>
        RefreshAsync(account, new(account.Id, RanksOnly: true), progress, cancellationToken);

    public Task RefreshAnalysisAsync(RiotAccount account, string queue, int count, IProgress<string> progress, CancellationToken cancellationToken) =>
        RefreshAsync(account, new(account.Id, queue, count), progress, cancellationToken);

    // Copies whatever the server has saved; no refresh is requested, so it never waits for the Riot API.
    public async Task<WebSyncResult> SyncAllAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Web版から同期中…");
        var remote = (await client.SnapshotAsync(ct)).Accounts.ToDictionary(a => a.Id);
        var imported = 0;
        var skipped = new List<string>();
        foreach (var account in store.Accounts())
        {
            ct.ThrowIfCancellationRequested();
            if (!remote.TryGetValue(account.Id, out var web)) skipped.Add($"{account.Label}（Web版に未登録）");
            else if (!SameIdentity(account.Lol, web.Lol)) skipped.Add($"{account.Label}（Web版とRiot ID・サーバーが異なる）");
            else { store.ReplaceLolData(account.Id, web.Lol.Puuid, web.Cache); imported++; }
        }
        return new(imported, skipped);
    }

    private async Task RefreshAsync(RiotAccount account, WebRefreshRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        // Registering first lets the server refresh an account added or edited here.
        progress?.Report("Web版にアカウントを登録中…");
        await client.SaveAccountAsync(account, ct);
        var job = await client.RefreshAsync(request, ct);
        while (!job.Finished)
        {
            progress?.Report(job.State == WebJob.Queued ? "Web版で順番待ち中（自動更新の実行中など）…" : $"Web版で更新中：{job.Progress ?? "開始しています…"}");
            await Task.Delay(poll, ct);
            job = await client.JobAsync(job.Id, ct);
        }
        // A failed job may still have saved some steps, so copy what the server has before reporting the failure.
        Import(account, await client.AccountAsync(account.Id, ct));
        if (job.State == WebJob.Failed) throw new WebSourceException(job.Message ?? "Web版での更新に失敗しました。");
        progress?.Report(job.Message ?? "更新しました。");
    }

    private void Import(RiotAccount account, WebAccount web)
    {
        if (!SameIdentity(account.Lol, web.Lol)) throw new WebSourceException("Web版のRiot ID・サーバーがこのアカウントと異なるため、取り込みませんでした。");
        store.ReplaceLolData(account.Id, web.Lol.Puuid, web.Cache);
    }

    // Read-only check of the server for the diagnosis dialog; nothing is saved and the server does not call Riot.
    public async Task<DiagnosticReport> DiagnoseAsync(RiotAccount account, CancellationToken ct)
    {
        string[] names = ["Web版への接続", "APIキー（Web版）", "アカウント（Web版）", "自動更新（Web版）"];
        var steps = new List<DiagnosticStep>();
        WebStatus? status = null;
        async Task<bool> Step(string name, Func<Task<string>> run)
        {
            var timer = Stopwatch.StartNew();
            try { steps.Add(new(name, true, await run(), timer.ElapsedMilliseconds)); return true; }
            catch (WebSourceException error) { steps.Add(new(name, false, error.Message, timer.ElapsedMilliseconds)); return false; }
        }
        _ = await Step(names[0], async () => { status = await client.StatusAsync(ct); return $"接続できました（版 {status.Version}）。"; })
            && await Step(names[1], () => status!.KeyRegistered ? Task.FromResult("登録済みです（値は表示しません）。")
                : throw new WebSourceException("未登録です。Web版の設定画面で登録してください。"))
            && await Step(names[2], async () =>
            {
                WebAccount web;
                try { web = await client.AccountAsync(account.Id, ct); }
                catch (WebSourceException error) when (error.StatusCode == HttpStatusCode.NotFound)
                { throw new WebSourceException("Web版に未登録です。「戦績・分析更新」を押すか、アカウントを保存すると登録します。"); }
                if (!SameIdentity(account.Lol, web.Lol)) throw new WebSourceException("Web版のRiot ID・サーバーがこのアカウントと異なります。");
                var last = new[] { web.Cache.Ranks.LastOrDefault()?.ObservedAt, web.Cache.MatchesUpdatedAt }.Max();
                return last is { } at ? $"登録済みです。最終取得 {at.ToLocalTime():yyyy/MM/dd HH:mm}" : "登録済みです。まだ取得していません。";
            })
            && await Step(names[3], () => status!.LastRun switch
            {
                null => Task.FromResult("まだ実行されていません。"),
                { Stop: RefreshStop.Key } => throw new WebSourceException("前回の自動更新でAPIキーが拒否されました。Web版の設定画面でキーを確認してください。"),
                var run => Task.FromResult($"前回 {run.CompletedAt.ToLocalTime():yyyy/MM/dd HH:mm}（{(run.Stop == RefreshStop.None && run.Failures == 0 ? "成功" : "一部失敗または中断")}）／次回 "
                    + (status!.NextDue <= DateTimeOffset.UtcNow ? "まもなく" : $"{status.NextDue.ToLocalTime():yyyy/MM/dd HH:mm}"))
            });
        return new("Web版（自宅サーバー）", $"{account.Label}（{account.RiotId}）", steps, names.Skip(steps.Count).ToList());
    }

    private static bool SameIdentity(GameProfile local, GameProfile web) =>
        string.Equals(local.GameName, web.GameName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(local.TagLine, web.TagLine, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(local.Platform, web.Platform, StringComparison.OrdinalIgnoreCase);
}
