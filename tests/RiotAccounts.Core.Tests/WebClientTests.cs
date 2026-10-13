using System.Net;
using System.Text.Json;
using RiotAccounts.Core;
using Xunit;
using static WebTestData;

public sealed class WebClientTests
{
    static WebStatsProvider Provider(StoreFixture fixture, WebTestServer server, string url = "https://web.example:8446/") =>
        new(fixture.Store, new WebStatsClient(server.Http, () => url), TimeSpan.Zero);

    [Fact]
    public async Task RefreshRegistersTheAccountWaitsForTheJobAndImportsTheResult()
    {
        using var fixture = new StoreFixture(); using var server = new WebTestServer();
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
        // After a server refresh its PUUID is adopted, so the records keyed by the old one are replaced by the server's.
        Assert.Equal("remote", fixture.Store.Accounts().Single().Lol.Puuid);
        Assert.Equal("JP1_2", Assert.Single(fixture.Store.Cache(account.Id).Matches).Id);
        Assert.Equal(new Credentials("secret-user", "secret-pass"), fixture.Store.Credentials(account.Id));
    }

    [Fact]
    public async Task FailedJobStillImportsWhatTheServerSavedAndReportsItsMessage()
    {
        using var fixture = new StoreFixture(); using var server = new WebTestServer();
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
        using var fixture = new StoreFixture(); using var server = new WebTestServer();
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
    public async Task ClientExplainsMissingUrlServerErrorsAndLostConnections()
    {
        using var server = new WebTestServer();
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
        using var fixture = new StoreFixture(); using var server = new WebTestServer();
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
