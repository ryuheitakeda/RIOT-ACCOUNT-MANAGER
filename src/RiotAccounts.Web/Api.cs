using RiotAccounts.Core;

namespace RiotAccounts.Web;

// JSON endpoints for the WPF "Web" source. Reads never call the Riot API; only /api/refresh queues work for the refresh worker.
public static class Api
{
    public static void MapApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/status", (RefreshWorker worker) => worker.Status());
        api.MapGet("/snapshot", (Store store) => new WebSnapshot(DateTimeOffset.UtcNow, [.. store.Accounts().Select(a => View(store, a))]));
        api.MapGet("/accounts/{id:guid}", (Guid id, Store store) =>
            store.Accounts().SingleOrDefault(a => a.Id == id) is { } account ? Results.Ok(View(store, account)) : Error(404, "アカウントがWeb版に登録されていません。"));
        api.MapPut("/accounts/{id:guid}", (Guid id, WebAccountInput input, Store store, RefreshWorker worker) =>
        {
            try { return Results.Ok(View(store, AccountEdits.Save(store, worker, id, input))); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return Error(400, error.Message); }
        });
        api.MapPost("/refresh", (WebRefreshRequest request, Store store, RefreshWorker worker, ApiKey key, WebSettings settings) =>
        {
            if (!key.Registered) return Error(409, "Web版にRiot APIキーが登録されていません。Web版の設定画面で登録してください。");
            if (request.AccountId is { } id && store.Accounts().All(a => a.Id != id)) return Error(404, "アカウントがWeb版に登録されていません。");
            if (request.Queue != null && Queues.Definitions.All(q => q.Key != request.Queue)) return Error(400, "非対応のキューです。");
            var count = request.Count ?? settings.MatchCount;
            if (count is not (20 or 50)) return Error(400, "取得件数は20か50を指定してください。");
            var job = worker.Enqueue(new RefreshTarget(request.AccountId, request.Queue, count, request.RanksOnly));
            return Results.Accepted($"/api/jobs/{job.Id}", job);
        });
        api.MapGet("/jobs/{id:guid}", (Guid id, RefreshWorker worker) =>
            worker.Job(id) is { } job ? Results.Ok(job) : Error(404, "更新ジョブが見つかりません（Web版が再起動した可能性があります）。"));
    }

    public static WebAccount View(Store store, RiotAccount account) => new(account.Id, account.Label, account.Note, account.Lol, store.Cache(account.Id));

    private static IResult Error(int status, string message) => Results.Json(new WebError(message), statusCode: status);
}

// Account edits from the pages and the API. The server only knows the non-secret fields.
public static class AccountEdits
{
    public static RiotAccount Save(Store store, RefreshWorker worker, Guid id, WebAccountInput input)
    {
        if (id == Guid.Empty) throw new ArgumentException("アカウントIDが不正です。");
        var label = (input.Label ?? "").Trim();
        var gameName = (input.GameName ?? "").Trim();
        var tagLine = (input.TagLine ?? "").Trim().TrimStart('#');
        var platform = (input.Platform ?? "").Trim().ToUpperInvariant();
        if (label.Length == 0) throw new ArgumentException("表示名を入力してください。");
        if (gameName.Length == 0 || tagLine.Length == 0) throw new ArgumentException("Riot ID（ゲーム名とタグ）を入力してください。");
        if (!Regions.Routing.ContainsKey(platform)) throw new ArgumentException("サーバーを選択してください。");
        var existing = store.Accounts().SingleOrDefault(a => a.Id == id);
        var lol = new GameProfile("lol", gameName, tagLine, platform, existing?.Lol.Puuid);
        // Store.Save drops the old matches on an identity change; a running refresh would write them back.
        if (existing != null && !SameIdentity(existing.Lol, lol) && worker.Touches(id))
            throw new InvalidOperationException("更新中はRiot IDとサーバーを変更できません。更新の完了後にもう一度保存してください。");
        var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        store.Save(new RiotAccount(id, label, [.. existing?.Profiles.Where(p => p.Game != "lol") ?? [], lol], note));
        worker.MarkChanged();
        return store.Accounts().Single(a => a.Id == id);
    }

    public static void Delete(Store store, RefreshWorker worker, Guid id)
    {
        if (worker.Touches(id)) throw new InvalidOperationException("更新中のアカウントは削除できません。更新の完了後に削除してください。");
        store.Delete(id);
        worker.MarkChanged();
    }

    private static bool SameIdentity(GameProfile left, GameProfile right) =>
        left.GameName == right.GameName && left.TagLine == right.TagLine && string.Equals(left.Platform, right.Platform, StringComparison.OrdinalIgnoreCase);
}
