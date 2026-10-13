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

public enum WebAccountImportState { Merged, Unchanged, Registered, PuuidMismatch, IdentityMismatch, Failed }

public sealed record WebAccountImport(Guid Id, string Label, WebAccountImportState State, LolMergeResult? Merge = null, string? Reason = null)
{
    public string Detail => State switch
    {
        WebAccountImportState.Merged => $"{Label}: 取り込み {Merge!.Summary}",
        WebAccountImportState.Unchanged => $"{Label}: 変更なし",
        _ => $"{Label}: {Reason}"
    };
}

public enum WebImportState { Imported, Unchanged, ServerBusy }

/// <summary>What one import from the web version did. Only <see cref="WebImportState.Imported"/> runs read the snapshot.</summary>
public sealed record WebImportResult(WebImportState State, WebStatus Status, string Marker, DateTimeOffset At, List<WebAccountImport> Accounts)
{
    public int Ranks => Accounts.Sum(a => a.Merge?.Ranks ?? 0);
    public int Matches => Accounts.Sum(a => a.Merge?.Matches ?? 0);
    public int UpgradedMatches => Accounts.Sum(a => a.Merge?.UpgradedMatches ?? 0);
    public int Opponents => Accounts.Sum(a => a.Merge?.Opponents ?? 0);
    public int Forecasts => Accounts.Sum(a => a.Merge?.Forecasts ?? 0);
    public int Registered => Accounts.Count(a => a.State == WebAccountImportState.Registered);
    public int Skipped => Accounts.Count(a => IsSkip(a.State));

    /// <summary>Counts only, e.g. "試合+5 ランク+3（2件）" or "変更なし".</summary>
    public string Summary
    {
        get
        {
            var counts = LolMergeResult.Describe(Matches, UpgradedMatches, Ranks, Opponents, Forecasts);
            return Accounts.Any(a => a.Merge?.Changes > 0) ? $"{counts}（{Accounts.Count(a => a.Merge?.Changes > 0)}件）" : counts;
        }
    }

    public string Message => State switch
    {
        WebImportState.ServerBusy => "Web版が更新中のため、取り込みは完了後に行います。",
        WebImportState.Unchanged => "Web版に新しい取得結果はありません。",
        _ => $"Web版から取り込み：{Summary}" + (Registered > 0 ? $"／登録 {Registered}件" : "")
            + (Skipped > 0 ? $"／未取込 {Skipped}件（{string.Join("・", SkipKinds.Where(k => Accounts.Any(a => a.State == k.State)).Select(k => k.Name))}）" : "")
    };

    /// <summary>One line per account, for a tooltip.</summary>
    public string Detail => string.Join(Environment.NewLine, Accounts.Select(a => a.Detail));

    private static readonly (WebAccountImportState State, string Name)[] SkipKinds =
        [(WebAccountImportState.IdentityMismatch, "Riot ID相違"), (WebAccountImportState.PuuidMismatch, "PUUID相違"), (WebAccountImportState.Failed, "登録失敗")];
    private static bool IsSkip(WebAccountImportState state) => SkipKinds.Any(k => k.State == state);
}

/// <summary>
/// The WPF side of the web version. Imports merge what the server has saved into the local store without asking it to
/// refresh, so they never wait for the Riot API; refreshes (the "Web" source's buttons) run on the server, the only caller
/// of the Riot API, and their results are merged back the same way. Accounts are matched by ID (shared since the DB was
/// migrated) and only while the Riot ID and server agree.
/// </summary>
public sealed class WebStatsProvider(Store store, WebStatsClient client, TimeSpan? pollInterval = null, DiagnosticLog? log = null) : IGameStatsProvider
{
    private readonly TimeSpan poll = pollInterval ?? TimeSpan.FromSeconds(1);
    private const string Stage = "取り込み";

    public Task RefreshRanksAsync(RiotAccount account, CancellationToken cancellationToken, IProgress<string>? progress = null) =>
        RefreshAsync(account, new(account.Id, RanksOnly: true), progress, cancellationToken);

    public Task RefreshAnalysisAsync(RiotAccount account, string queue, int count, IProgress<string> progress, CancellationToken cancellationToken) =>
        RefreshAsync(account, new(account.Id, queue, count), progress, cancellationToken);

    /// <summary>
    /// Merges the server's saved data into every local account. Without <paramref name="force"/> the snapshot is skipped while a
    /// server job runs (its records are mid-write) or when the status marker still equals <paramref name="sinceMarker"/>.
    /// Accounts the server does not know are registered with their non-secret fields so its next scheduled run covers them.
    /// </summary>
    public async Task<WebImportResult> ImportAllAsync(string? sinceMarker, bool force, IProgress<string>? progress, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        log?.Write(Stage, "Web版 開始");
        try
        {
            progress?.Report("Web版の状態を確認中…");
            var status = await client.StatusAsync(ct);
            var now = DateTimeOffset.UtcNow;
            if (!force && status.Current != null) { log?.Write(Stage, "Web版 更新中のため見送り"); return new(WebImportState.ServerBusy, status, status.ChangeMarker, now, []); }
            if (!force && status.ChangeMarker == sinceMarker) { log?.Write(Stage, "Web版 変更なし"); return new(WebImportState.Unchanged, status, status.ChangeMarker, now, []); }
            progress?.Report("Web版から取り込み中…");
            var remote = (await client.SnapshotAsync(ct)).Accounts.ToDictionary(a => a.Id);
            var accounts = new List<WebAccountImport>();
            foreach (var account in store.Accounts())
            {
                ct.ThrowIfCancellationRequested();
                accounts.Add(await ImportOne(account, remote, ct));
            }
            var result = new WebImportResult(WebImportState.Imported, status, status.ChangeMarker, now, accounts);
            log?.Write(Stage, $"Web版 完了（{timer.ElapsedMilliseconds}ms 試合+{result.Matches} 補完{result.UpgradedMatches} ランク+{result.Ranks} 相手+{result.Opponents} 予測+{result.Forecasts} 登録{result.Registered} 未取込{result.Skipped}）");
            return result;
        }
        catch (OperationCanceledException) { log?.Write(Stage, "Web版 中止"); throw; }
        catch (Exception error) { log?.Write(Stage, $"Web版 失敗 {error.GetType().Name}"); throw; }
    }

    private async Task<WebAccountImport> ImportOne(RiotAccount account, Dictionary<Guid, WebAccount> remote, CancellationToken ct)
    {
        if (!remote.TryGetValue(account.Id, out var web))
        {
            // Registering lets the server's next scheduled run cover an account added here; credentials never leave this PC.
            try { await client.SaveAccountAsync(account, ct); return new(account.Id, account.Label, WebAccountImportState.Registered, Reason: "Web版に登録しました。次回の自動更新から取得します。"); }
            catch (WebSourceException error) { return new(account.Id, account.Label, WebAccountImportState.Failed, Reason: $"Web版に登録できません（{error.Message}）"); }
        }
        if (!SameIdentity(account.Lol, web.Lol)) return new(account.Id, account.Label, WebAccountImportState.IdentityMismatch, Reason: "Web版とRiot ID・サーバーが異なります。");
        // The merge reads and rewrites the account's records, so it runs off the caller's (UI) thread.
        var merge = await Task.Run(() => store.MergeLolData(account.Id, web.Lol.Puuid, web.Cache), ct);
        return merge.Outcome switch
        {
            LolMergeOutcome.PuuidMismatch => new(account.Id, account.Label, WebAccountImportState.PuuidMismatch, merge,
                OpggStatsProvider.Identity(store, account.Id)?.Puuid == account.Lol.Puuid
                    ? "PUUIDがOP.GG由来です。取得元Riot APIで一度更新すると揃います。" : "PUUIDがWeb版と異なります（別のAPIキーの可能性）。"),
            LolMergeOutcome.Merged => new(account.Id, account.Label, WebAccountImportState.Merged, merge),
            _ => new(account.Id, account.Label, WebAccountImportState.Unchanged, merge)
        };
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

    // After a refresh the server's copy is authoritative, so a PUUID it re-resolved replaces the local one.
    private void Import(RiotAccount account, WebAccount web)
    {
        if (!SameIdentity(account.Lol, web.Lol)) throw new WebSourceException("Web版のRiot ID・サーバーがこのアカウントと異なるため、取り込みませんでした。");
        store.MergeLolData(account.Id, web.Lol.Puuid, web.Cache, adoptServerPuuid: true);
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
                { throw new WebSourceException("Web版に未登録です。「Web版から取り込み」を押すか、アカウントを保存すると登録します。"); }
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
