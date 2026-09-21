using System.Net;
using System.Text;
using RiotAccounts.Core;
using Xunit;

// Synthetic payloads only; no network access.
public sealed class StatsDiagnosticsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "riot-diag-" + Guid.NewGuid().ToString("N"));
    private string LogPath => Path.Combine(directory, "diagnostics.log");
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private static readonly InlineProgress Quiet = new(_ => { });
    private static RiotAccount Account() => new(Guid.NewGuid(), "Main", [new("lol", "Player", "JP1", "JP1", "old-puuid")]);

    private sealed class Routes(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage RiotRoute(HttpRequestMessage r)
    {
        var path = r.RequestUri!.AbsolutePath;
        if (path.Contains("by-riot-id")) return Ok("""{"puuid":"SECRET-PUUID"}""");
        if (path.Contains("league/v4")) return Ok("[]");
        if (path.Contains("by-puuid")) return Ok("""["JP1_100"]""");
        return Ok("""{"info":{}}""");
    }

    [Fact]
    public void Redact_KeepsOnlyRouteTemplate()
    {
        Assert.Equal("/lol/match/v5/matches/by-puuid/{id}/ids", ApiRoute.Redact("/lol/match/v5/matches/by-puuid/SECRET/ids?queue=420&start=0"));
        Assert.Equal("/riot/account/v1/accounts/by-riot-id/{id}/{id}", ApiRoute.Redact("/riot/account/v1/accounts/by-riot-id/Faker/KR1"));
        Assert.Equal("/api/jp/summoners/{id}/games", ApiRoute.Redact("/api/jp/summoners/abc123/games?limit=20&ended_at=2026"));
    }

    [Fact]
    public async Task RiotApi_LogsStatusAndRouteWithoutIdentifiers()
    {
        var log = new DiagnosticLog(LogPath);
        using var api = new RiotApi(new HttpClient(new Routes(_ => Ok("[]"))), () => "RGAPI-SECRET-KEY", log);
        await api.GetAsync<List<string>>("asia", "/lol/match/v5/matches/by-puuid/SECRET-PUUID/ids?count=1", CancellationToken.None);
        var text = File.ReadAllText(LogPath);
        Assert.Contains("asia /lol/match/v5/matches/by-puuid/{id}/ids -> HTTP 200", text);
        Assert.DoesNotContain("SECRET", text);
    }

    [Fact]
    public async Task Riot_AllStagesPass()
    {
        var handler = new Routes(RiotRoute);
        using var riot = new RiotApi(new HttpClient(handler), () => "RGAPI-KEY");
        using var opgg = new OpggApi(new HttpClient(handler), TimeSpan.Zero);
        var report = await new StatsDiagnostics(riot, opgg, () => "RGAPI-KEY").RunAsync(Account(), "riot", Quiet, CancellationToken.None);
        Assert.True(report.Ok);
        Assert.Equal(5, report.Steps.Count);
        Assert.Empty(report.Skipped);
        Assert.Contains("保存済みのPUUIDと一致しません", report.Steps[1].Detail);
        Assert.DoesNotContain("SECRET", report.ToText());
    }

    [Fact]
    public async Task Riot_MissingKeyStopsBeforeAnyRequest()
    {
        var handler = new Routes(RiotRoute);
        using var riot = new RiotApi(new HttpClient(handler), () => null);
        using var opgg = new OpggApi(new HttpClient(handler), TimeSpan.Zero);
        var report = await new StatsDiagnostics(riot, opgg, () => null).RunAsync(Account(), "riot", Quiet, CancellationToken.None);
        Assert.False(report.Ok);
        Assert.Equal("APIキー", report.Failed!.Name);
        Assert.Empty(handler.Requested);
        Assert.Equal(4, report.Skipped.Count);
    }

    [Fact]
    public async Task Riot_ExpiredKeyReportsHttpStatusAtTheFailingStage()
    {
        var handler = new Routes(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var log = new DiagnosticLog(LogPath);
        using var riot = new RiotApi(new HttpClient(handler), () => "RGAPI-OLD", log);
        using var opgg = new OpggApi(new HttpClient(handler), TimeSpan.Zero);
        var report = await new StatsDiagnostics(riot, opgg, () => "RGAPI-OLD", log).RunAsync(Account(), "riot", Quiet, CancellationToken.None);
        Assert.Equal("Riot IDの解決", report.Failed!.Name);
        Assert.Contains("HTTP 403", report.Failed.Detail);
        Assert.Equal(["ランク取得", "試合一覧", "試合詳細"], report.Skipped);
        Assert.Contains("[診断]", File.ReadAllText(LogPath));
    }

    [Fact]
    public async Task Opgg_AllStagesPassAndSearchNeedsExactRiotId()
    {
        var handler = new Routes(r => r.RequestUri!.AbsolutePath switch
        {
            "/api/v3/jp/summoners" => Ok("""{"data":[{"game_name":"Someone","tagline":"JP1","summoner_id":"x"},{"game_name":"player","tagline":"jp1","summoner_id":"s1"}]}"""),
            "/api/jp/summoners/s1/summary" => Ok("""{"data":{}}"""),
            "/api/jp/summoners/s1/games" => Ok("""{"data":[]}"""),
            _ => Ok("""{"data":[{"id":1,"key":"Ahri"}]}""")
        });
        using var riot = new RiotApi(new HttpClient(handler), () => null);
        using var opgg = new OpggApi(new HttpClient(handler), TimeSpan.Zero);
        var report = await new StatsDiagnostics(riot, opgg, () => null).RunAsync(Account(), "opgg", Quiet, CancellationToken.None);
        Assert.True(report.Ok, report.ToText());
        Assert.Equal(4, report.Steps.Count);
    }

    [Fact]
    public async Task Opgg_ShapeChangeAndBlockAreReportedPerStage()
    {
        var handler = new Routes(r => r.RequestUri!.AbsolutePath.EndsWith("/summary")
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : Ok("""{"data":[{"game_name":"Player","tagline":"JP1","summoner_id":"s1"}]}"""));
        using var riot = new RiotApi(new HttpClient(handler), () => null);
        using var opgg = new OpggApi(new HttpClient(handler), TimeSpan.Zero);
        var report = await new StatsDiagnostics(riot, opgg, () => null).RunAsync(Account(), "opgg", Quiet, CancellationToken.None);
        Assert.Equal("ランク（summary）", report.Failed!.Name);
        Assert.Contains("HTTP 403", report.Failed.Detail);
    }

    [Fact]
    public async Task LoggedProvider_RecordsOutcomeWithoutAccountData()
    {
        var log = new DiagnosticLog(LogPath);
        var failing = new Throwing(new RiotApiException("キーが無効です", HttpStatusCode.Forbidden));
        var provider = new LoggedStatsProvider(failing, "riot", log);
        await Assert.ThrowsAsync<RiotApiException>(() => provider.RefreshRanksAsync(Account(), CancellationToken.None));
        var text = File.ReadAllText(LogPath);
        Assert.Contains("riot ランク更新 開始", text);
        Assert.Contains("失敗 HTTP 403", text);
        Assert.DoesNotContain("Player", text);
    }

    private sealed class Throwing(Exception error) : IGameStatsProvider
    {
        public Task RefreshRanksAsync(RiotAccount account, CancellationToken cancellationToken, IProgress<string>? progress = null) => Task.FromException(error);
        public Task RefreshAnalysisAsync(RiotAccount account, string queue, int count, IProgress<string> progress, CancellationToken cancellationToken) => Task.FromException(error);
    }
}
