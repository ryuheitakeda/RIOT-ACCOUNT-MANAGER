using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RiotAccounts.Core;
using Xunit;

public sealed class WebClientTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly DateTimeOffset At = new(2026, 10, 12, 3, 0, 0, TimeSpan.Zero);

    static HttpResponseMessage Reply(HttpStatusCode code, object body) => new(code) { Content = JsonContent.Create(body, body.GetType(), options: Json) };
    static RiotAccount Account(string label = "Main", string gameName = "Name", string? puuid = "local") => new(Guid.NewGuid(), label, [new("lol", gameName, "JP1", "JP1", puuid)]);
    static MatchRecord Match(string id, string puuid) => new(id, 420, At, 1800, false, [new(puuid, 100, "Ahri", "MIDDLE", 1, 2, 3, 100, 10, true)]);
    static WebAccount Remote(RiotAccount account, string puuid = "remote", string? gameName = null)
    {
        var cache = new AccountCache { MatchesUpdatedAt = At };
        cache.Ranks.Add(new(At, [new(Queues.Solo, "GOLD", "II", 40, 10, 8)]));
        cache.Matches.Add(Match("JP1_2", puuid));
        return new(account.Id, account.Label, account.Note, account.Lol with { Puuid = puuid, GameName = gameName ?? account.Lol.GameName }, cache);
    }
    static WebJob Job(Guid id, string state, string? progress = null, string? message = null) => new(id, state, progress, message, null, Queues.Solo, false, At, null, null);

    sealed record Call(string Method, string Path, string? Body);
    sealed class Server : IDisposable
    {
        private readonly Handler handler;
        public List<Call> Calls { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Route { get; set; } = _ => new(HttpStatusCode.NotFound);
        public HttpClient Http { get; }
        public Server()
        {
            handler = new(async (request, ct) =>
            {
                Calls.Add(new(request.Method.Method, request.RequestUri!.AbsolutePath, request.Content == null ? null : await request.Content.ReadAsStringAsync(ct)));
                return Route(request);
            });
            Http = new(handler);
        }
        public void Dispose() { Http.Dispose(); handler.Dispose(); }
    }

    sealed class Reports : IProgress<string> { public List<string> Values { get; } = []; public void Report(string value) => Values.Add(value); }

    static WebStatsProvider Provider(StoreFixture fixture, Server server, string url = "https://web.example:8446/") =>
        new(fixture.Store, new WebStatsClient(server.Http, () => url), TimeSpan.Zero);

    [Fact]
    public void ReplaceLolDataSwapsPuuidAndRecordsButKeepsCredentialsAndOtherProfiles()
    {
        using var fixture = new StoreFixture();
        var account = new RiotAccount(Guid.NewGuid(), "Main", [new("lol", "Name", "JP1", "JP1", "old"), new("tft", "Name", "JP1", "JP1")]);
        fixture.Store.Save(account, new("user", "pass"));
        var old = new AccountCache(); old.Matches.Add(Match("JP1_1", "old")); fixture.Store.SaveCache(account.Id, old);

        fixture.Store.ReplaceLolData(account.Id, "remote", Remote(account).Cache);

        var saved = fixture.Store.Accounts().Single();
        Assert.Equal("remote", saved.Lol.Puuid);
        Assert.Contains(saved.Profiles, p => p.Game == "tft");
        Assert.Equal(new Credentials("user", "pass"), fixture.Store.Credentials(account.Id));
        var cache = fixture.Store.Cache(account.Id);
        Assert.Equal("JP1_2", Assert.Single(cache.Matches).Id);
        Assert.Single(cache.Ranks);
        Assert.Equal(At, cache.MatchesUpdatedAt);
        Assert.Throws<InvalidOperationException>(() => fixture.Store.ReplaceLolData(Guid.NewGuid(), "x", new()));
    }

    [Fact]
    public async Task RefreshRegistersTheAccountWaitsForTheJobAndImportsTheResult()
    {
        using var fixture = new StoreFixture(); using var server = new Server();
        var account = Account() with { Note = "メモ" }; fixture.Store.Save(account, new("secret-user", "secret-pass"));
        var jobId = Guid.NewGuid(); var polls = 0;
        server.Route = request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("PUT", var path) when path == $"/api/accounts/{account.Id}" => Reply(HttpStatusCode.OK, Remote(account)),
            ("POST", "/api/refresh") => Reply(HttpStatusCode.Accepted, Job(jobId, WebJob.Queued)),
            ("GET", var path) when path == $"/api/jobs/{jobId}" => Reply(HttpStatusCode.OK, ++polls == 1
                ? Job(jobId, WebJob.Running, "参加者の現在ランクを取得中 1/9") : Job(jobId, WebJob.Succeeded, message: "3件を更新しました。")),
            ("GET", var path) when path == $"/api/accounts/{account.Id}" => Reply(HttpStatusCode.OK, Remote(account)),
            _ => new(HttpStatusCode.NotFound)
        };
        var progress = new Reports();

        await Provider(fixture, server).RefreshAnalysisAsync(account, Queues.Normal, 50, progress, default);

        Assert.Equal(new[] { "PUT", "POST", "GET", "GET", "GET" }, server.Calls.Select(c => c.Method));
        Assert.Equal(new WebAccountInput("Main", "Name", "JP1", "JP1", "メモ"), JsonSerializer.Deserialize<WebAccountInput>(server.Calls[0].Body!, Json));
        Assert.DoesNotContain("secret", server.Calls[0].Body!);
        Assert.Contains("\"queue\":\"NORMAL\"", server.Calls[1].Body!); Assert.Contains("\"count\":50", server.Calls[1].Body!);
        Assert.Contains("Web版で更新中：参加者の現在ランクを取得中 1/9", progress.Values);
        Assert.Equal("3件を更新しました。", progress.Values.Last());
        Assert.Equal("remote", fixture.Store.Accounts().Single().Lol.Puuid);
        Assert.Equal("JP1_2", Assert.Single(fixture.Store.Cache(account.Id).Matches).Id);
        Assert.Equal(new Credentials("secret-user", "secret-pass"), fixture.Store.Credentials(account.Id));
    }

    [Fact]
    public async Task FailedJobStillImportsWhatTheServerSavedAndReportsItsMessage()
    {
        using var fixture = new StoreFixture(); using var server = new Server();
        var account = Account(); fixture.Store.Save(account);
        var jobId = Guid.NewGuid();
        server.Route = request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("PUT", _) => Reply(HttpStatusCode.OK, Remote(account)),
            ("POST", _) => Reply(HttpStatusCode.Accepted, Job(jobId, WebJob.Failed, message: "取得制限が続いたため中断しました。")),
            ("GET", var path) when path.StartsWith("/api/accounts/") => Reply(HttpStatusCode.OK, Remote(account)),
            _ => new(HttpStatusCode.NotFound)
        };

        var error = await Assert.ThrowsAsync<WebSourceException>(() => Provider(fixture, server).RefreshRanksAsync(account, default));

        Assert.Equal("取得制限が続いたため中断しました。", error.Message);
        Assert.Contains("\"ranksOnly\":true", server.Calls[1].Body!);
        Assert.Single(fixture.Store.Cache(account.Id).Matches);
    }

    [Fact]
    public async Task ServerCopyWithAnotherRiotIdIsNotImported()
    {
        using var fixture = new StoreFixture(); using var server = new Server();
        var account = Account(); fixture.Store.Save(account);
        var jobId = Guid.NewGuid();
        server.Route = request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("PUT", _) => Reply(HttpStatusCode.OK, Remote(account)),
            ("POST", _) => Reply(HttpStatusCode.Accepted, Job(jobId, WebJob.Succeeded)),
            ("GET", _) => Reply(HttpStatusCode.OK, Remote(account, gameName: "Someone Else")),
            _ => new(HttpStatusCode.NotFound)
        };

        await Assert.ThrowsAsync<WebSourceException>(() => Provider(fixture, server).RefreshRanksAsync(account, default));

        Assert.Equal("local", fixture.Store.Accounts().Single().Lol.Puuid);
        Assert.Empty(fixture.Store.Cache(account.Id).Matches);
    }

    [Fact]
    public async Task SyncCopiesMatchingAccountsAndNamesTheOthers()
    {
        using var fixture = new StoreFixture(); using var server = new Server();
        var same = Account("Same"); var renamed = Account("Renamed"); var missing = Account("Missing");
        fixture.Store.Save(same); fixture.Store.Save(renamed); fixture.Store.Save(missing);
        server.Route = _ => Reply(HttpStatusCode.OK, new WebSnapshot(At, [Remote(same), Remote(renamed, gameName: "Changed")]));

        var result = await Provider(fixture, server).SyncAllAsync(null, default);

        Assert.Equal(1, result.Imported);
        Assert.Equal(new[] { "Missing（Web版に未登録）", "Renamed（Web版とRiot ID・サーバーが異なる）" }, result.Skipped.Order());
        Assert.Equal("remote", fixture.Store.Accounts().Single(a => a.Id == same.Id).Lol.Puuid);
        Assert.Empty(fixture.Store.Cache(renamed.Id).Matches);
        Assert.Contains("同期しなかったアカウント", result.Message);
        Assert.Equal(new[] { "GET" }, server.Calls.Select(c => c.Method));
    }

    [Fact]
    public async Task ClientExplainsMissingUrlServerErrorsAndLostConnections()
    {
        using var server = new Server();
        var none = await Assert.ThrowsAsync<WebSourceException>(() => new WebStatsClient(server.Http, () => " ").StatusAsync(default));
        Assert.Equal("設定でWeb版のURLを登録してください。", none.Message);
        Assert.Empty(server.Calls);

        server.Route = _ => Reply(HttpStatusCode.Conflict, new WebError("Web版にRiot APIキーが登録されていません。"));
        var conflict = await Assert.ThrowsAsync<WebSourceException>(() => new WebStatsClient(server.Http, () => "https://host/riot/").RefreshAsync(new(), default));
        Assert.Equal(("Web版にRiot APIキーが登録されていません。", HttpStatusCode.Conflict), (conflict.Message, conflict.StatusCode));
        Assert.Equal("/riot/api/refresh", server.Calls.Single().Path);

        server.Route = _ => throw new HttpRequestException("refused");
        var down = await Assert.ThrowsAsync<WebSourceException>(() => new WebStatsClient(server.Http, () => "https://host").StatusAsync(default));
        Assert.StartsWith("Web版に接続できません", down.Message);

        Assert.NotNull(WebStatsClient.BaseUri("https://my-mac.example.ts.net:8446/"));
        Assert.Null(WebStatsClient.BaseUri("my-mac:8446"));
        Assert.Null(WebStatsClient.BaseUri("ftp://host"));
        Assert.Null(WebStatsClient.BaseUri("https://host/?x=1"));
    }

    [Fact]
    public async Task DiagnosisChecksTheServerStepByStep()
    {
        using var fixture = new StoreFixture(); using var server = new Server();
        var account = Account(); fixture.Store.Save(account);
        var lastRun = new RefreshResult(At, At.AddMinutes(1), RefreshStop.Key, []);
        server.Route = request => request.RequestUri!.AbsolutePath == "/api/status"
            ? Reply(HttpStatusCode.OK, new WebStatus("1.3.0", true, null, 0, lastRun, At.AddHours(6), 6, []))
            : Reply(HttpStatusCode.OK, Remote(account));

        var report = await Provider(fixture, server).DiagnoseAsync(account, default);

        Assert.Equal(new[] { true, true, true, false }, report.Steps.Select(s => s.Ok));
        Assert.Contains("APIキーが拒否されました", report.Failed!.Detail);

        server.Route = request => request.RequestUri!.AbsolutePath == "/api/status"
            ? Reply(HttpStatusCode.OK, new WebStatus("1.3.0", true, null, 0, null, DateTimeOffset.MinValue, 6, []))
            : Reply(HttpStatusCode.NotFound, new WebError("アカウントがWeb版に登録されていません。"));
        var unknown = await Provider(fixture, server).DiagnoseAsync(account, default);
        Assert.Equal(new[] { true, true, false }, unknown.Steps.Select(s => s.Ok));
        Assert.Equal(new[] { "自動更新（Web版）" }, unknown.Skipped);
        Assert.StartsWith("Web版に未登録です", unknown.Failed!.Detail);
    }
}
