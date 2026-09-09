using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RiotAccounts.Core;
using Xunit;

public sealed class NormalMatchesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private static RiotAccount Account(string? puuid = "self") => new(Guid.NewGuid(), "Main", [new("lol", "Player", "JP1", "JP1", puuid)]);
    private static MatchRecord Match(int index, int queue = 400, bool remake = false) => new(
        $"JP1_{index}", queue, Now.AddMinutes(-index), remake ? 180 : 1800, remake,
        [new("self", 100, "Ahri", "MIDDLE", 5, 2, 7, 200, 20, index % 2 == 0)]);
    private static HttpResponseMessage Response(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json")
    };
    private static object ApiMatch(MatchRecord match) => new
    {
        metadata = new { matchId = match.Id },
        info = new
        {
            queueId = match.QueueId, gameStartTimestamp = match.StartedAt.ToUnixTimeMilliseconds(), gameDuration = match.DurationSeconds,
            participants = match.Participants.Select(p => new
            {
                puuid = p.Puuid, teamId = p.TeamId, championName = p.Champion, teamPosition = p.Role,
                kills = p.Kills, deaths = p.Deaths, assists = p.Assists, totalMinionsKilled = p.Cs,
                neutralMinionsKilled = 0, visionScore = p.VisionScore, win = p.Win, gameEndedInEarlySurrender = match.Remake
            })
        }
    };
    private static Dictionary<string, int> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(p => p.Split('=')).ToDictionary(p => p[0], p => int.Parse(p[1]));

    private sealed class MatchHandler(List<MatchRecord> matches) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public Func<HttpRequestMessage, CancellationToken, HttpResponseMessage?>? Intercept { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            Assert.DoesNotContain("/league/", uri.AbsolutePath);
            var intercepted = Intercept?.Invoke(request, ct);
            if (intercepted != null) return Task.FromResult(intercepted);
            Assert.Equal("asia.api.riotgames.com", uri.Host);
            if (uri.AbsolutePath.EndsWith("/ids"))
            {
                var query = Query(uri);
                Assert.Contains(query["queue"], Queues.Get(Queues.Normal).QueueIds);
                return Task.FromResult(Response(matches.Where(m => m.QueueId == query["queue"]).OrderByDescending(m => m.StartedAt)
                    .Skip(query["start"]).Take(query["count"]).Select(m => m.Id).ToArray()));
            }
            return Task.FromResult(Response(ApiMatch(matches.First(m => uri.AbsolutePath.EndsWith('/' + m.Id)))));
        }
    }

    [Fact]
    public void NormalDefinitionAndForecastGuard()
    {
        Assert.Equal(new[] { 400, 430, 480, 490 }, Queues.Get(Queues.Normal).QueueIds);
        Assert.False(Queues.IsRanked(Queues.Normal));
        Assert.True(Queues.IsRanked(Queues.Solo));
        Assert.True(Queues.IsRanked(Queues.Flex));
        Assert.Equal(420, Queues.Id(Queues.Solo));
        Assert.Equal(440, Queues.Id(Queues.Flex));
        Assert.False(Queues.Includes(Queues.Normal, 450));
        Assert.All(Queues.Get(Queues.Normal).QueueIds, id => Assert.DoesNotContain("キュー ", Queues.MatchName(id)));
        Assert.Throws<ArgumentException>(() => Queues.Id(Queues.Normal));
        Assert.Throws<ArgumentException>(() => Analytics.Predict(new(), "self", Queues.Normal, Now));
        Assert.Throws<ArgumentException>(() => Analytics.Recent(new(), "self", "unknown", 20));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    public void NormalAnalyticsCombinesQueuesWithoutOtherModesOrRemakes(int count)
    {
        var modes = Queues.Get(Queues.Normal).QueueIds;
        var matches = Enumerable.Range(0, 80).Select(i => Match(i, modes[i % 4], i % 9 == 0)).ToList();
        var expected = matches.Where(m => !m.Remake).Take(count).Select(m => m.Id).ToList();
        var cache = new AccountCache { Matches = [.. matches.AsEnumerable().Reverse(), matches[1], Match(-1, 420), Match(-2, 440), Match(-3, 450),
            Match(-4) with { DurationSeconds = 0 }, Match(-5) with { Participants = [] }] };
        var recent = Analytics.Recent(cache, "self", Queues.Normal, count);
        Assert.Equal(expected, recent.Select(m => m.Id));
        var performance = Assert.Single(Analytics.Summarize(recent, "self", p => p.Champion));
        Assert.Equal(count, performance.Games);
        Assert.Equal(6, performance.Kda);
        Assert.Equal(recent.Count(m => m.Participants[0].Win), performance.Wins);
        Assert.Single(Analytics.Recent(cache, "self", Queues.Solo, count));
        Assert.Single(Analytics.Recent(cache, "self", Queues.Flex, count));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    public async Task RefreshMergesLatestNormalMatchesAndPreservesRankedData(int count)
    {
        using var fixture = new StoreFixture();
        var account = Account(); fixture.Store.Save(account);
        var rank = new RankEntry(Queues.Solo, "GOLD", "IV", 25, 10, 5);
        var old = new AccountCache
        {
            Matches = [Match(1000, 420)], Ranks = [new(Now, [rank])],
            Opponents = [new("enemy", Queues.Solo, Now, rank)],
            Forecasts = [Analytics.Predict(new(), "self", Queues.Solo, Now)],
            QueueUpdatedAt = new() { [Queues.Solo] = Now }
        };
        fixture.Store.SaveCache(account.Id, old);
        var modes = Queues.Get(Queues.Normal).QueueIds;
        var matches = Enumerable.Range(0, 80).Select(i => Match(i, modes[i % 4], i % 13 == 0)).ToList();
        using var handler = new MatchHandler(matches); using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
        await new LolStatsProvider(fixture.Store, api).RefreshAnalysisAsync(account, Queues.Normal, count, new InlineProgress(_ => { }), default);
        var cache = fixture.Store.Cache(account.Id);
        Assert.Equal(matches.Where(m => !m.Remake).Take(count).Select(m => m.Id), Analytics.Recent(cache, "self", Queues.Normal, count).Select(m => m.Id));
        Assert.Equal(rank, Assert.Single(Assert.Single(cache.Ranks).Entries));
        Assert.Equal(old.Opponents, cache.Opponents); Assert.Equal(old.Forecasts, cache.Forecasts);
        Assert.Equal(Now, cache.QueueUpdatedAt[Queues.Solo]);
        Assert.Equal(cache.MatchesUpdatedAt, cache.QueueUpdatedAt[Queues.Normal]);
        Assert.Single(Analytics.Recent(cache, "self", Queues.Solo, count));
        Assert.Equal(4, handler.Requests.Count(u => u.AbsolutePath.EndsWith("/ids")));
        var details = handler.Requests.Count(u => !u.AbsolutePath.EndsWith("/ids"));
        Assert.InRange(details, count, count + 3 + matches.Count(m => m.Remake));
        Assert.Equal(cache.Matches.Count, cache.Matches.DistinctBy(m => m.Id).Count());
    }

    [Fact]
    public async Task RemakesAndDuplicatesAreSkippedAndHistoryIsPagedToFillCount()
    {
        using var fixture = new StoreFixture(); var account = Account(); fixture.Store.Save(account);
        var matches = Enumerable.Range(0, 24).Select(i => Match(i, 480, i < 4)).ToList();
        matches.Insert(5, matches[4]);
        using var handler = new MatchHandler(matches); using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
        await new LolStatsProvider(fixture.Store, api).RefreshAnalysisAsync(account, Queues.Normal, 20, new InlineProgress(_ => { }), default);
        var cache = fixture.Store.Cache(account.Id);
        Assert.Equal(Enumerable.Range(4, 20).Select(i => $"JP1_{i}"), Analytics.Recent(cache, "self", Queues.Normal, 20).Select(m => m.Id));
        Assert.Equal(new[] { 0, 20 }, handler.Requests.Where(u => u.AbsolutePath.EndsWith("/ids") && Query(u)["queue"] == 480).Select(u => Query(u)["start"]));
        Assert.Equal(24, cache.Matches.Count);
        Assert.Equal(1, handler.Requests.Count(u => u.AbsolutePath.EndsWith("/JP1_4")));
    }

    [Fact]
    public async Task ShortHistoryIsReportedAndCachedDetailsAreReused()
    {
        using var fixture = new StoreFixture(); var account = Account(); fixture.Store.Save(account);
        var matches = new List<MatchRecord> { Match(0), Match(1, 490), Match(2, 480, true) };
        fixture.Store.SaveCache(account.Id, new() { Matches = [matches[0]] });
        using var handler = new MatchHandler(matches); using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
        var messages = new List<string>();
        await new LolStatsProvider(fixture.Store, api).RefreshAnalysisAsync(account, Queues.Normal, 20, new InlineProgress(messages.Add), default);
        Assert.Equal(2, Analytics.Recent(fixture.Store.Cache(account.Id), "self", Queues.Normal, 20).Count);
        Assert.DoesNotContain(handler.Requests, u => u.AbsolutePath.EndsWith("/JP1_0"));
        Assert.Contains("2/20戦", messages.Last());
    }

    [Fact]
    public async Task NormalResolvesIdentityWithoutRequestingRanks()
    {
        using var fixture = new StoreFixture();
        var account = Account(null) with { Profiles = [new("lol", "Player", "JP1", "JP1"), new("tft", "Other", "TAG", "JP1", "other")] };
        fixture.Store.Save(account);
        using var handler = new MatchHandler([])
        {
            Intercept = (request, _) => request.RequestUri!.AbsolutePath.StartsWith("/riot/account/")
                ? Response(new { puuid = "self", gameName = "Player", tagLine = "JP1" }) : null
        };
        using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
        await new LolStatsProvider(fixture.Store, api).RefreshAnalysisAsync(account, Queues.Normal, 20, new InlineProgress(_ => { }), default);
        var saved = Assert.Single(fixture.Store.Accounts());
        Assert.Equal("self", saved.Lol.Puuid); Assert.Equal(2, saved.Profiles.Count);
        var cache = fixture.Store.Cache(account.Id);
        Assert.Empty(cache.Ranks); Assert.Empty(cache.Opponents); Assert.Empty(cache.Forecasts);
        Assert.Contains(Queues.Normal, cache.QueueUpdatedAt.Keys);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartialCancellationOrFailureKeepsDownloadedMatchesAndOldUpdateTime(bool cancel)
    {
        using var fixture = new StoreFixture(); var account = Account(); fixture.Store.Save(account);
        fixture.Store.SaveCache(account.Id, new() { Matches = [Match(99, 420)], MatchesUpdatedAt = Now, QueueUpdatedAt = new() { [Queues.Normal] = Now } });
        using var cts = new CancellationTokenSource();
        using var handler = new MatchHandler([Match(0), Match(1)])
        {
            Intercept = (request, ct) =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/JP1_1")) return null;
                if (cancel) { cts.Cancel(); ct.ThrowIfCancellationRequested(); }
                return Response(new { }, HttpStatusCode.ServiceUnavailable);
            }
        };
        using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
        var refresh = () => new LolStatsProvider(fixture.Store, api).RefreshAnalysisAsync(account, Queues.Normal, 20, new InlineProgress(_ => { }), cts.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(refresh);
        else await Assert.ThrowsAsync<RiotApiException>(refresh);
        var cache = fixture.Store.Cache(account.Id);
        Assert.Equal(2, cache.Matches.Count); Assert.Equal("JP1_0", Assert.Single(Analytics.Recent(cache, "self", Queues.Normal, 20)).Id);
        Assert.Equal(Now, cache.MatchesUpdatedAt); Assert.Equal(Now, cache.QueueUpdatedAt[Queues.Normal]);
        Assert.Empty(cache.Ranks); Assert.Empty(cache.Opponents); Assert.Empty(cache.Forecasts);
    }

    [Theory]
    [InlineData(420, "self")]
    [InlineData(450, "self")]
    [InlineData(480, "self")]
    [InlineData(400, "another")]
    public async Task MismatchedQueueOrAccountIsRejectedBeforeSaving(int queueId, string puuid)
    {
        using var fixture = new StoreFixture(); var account = Account(); fixture.Store.Save(account);
        var valid = Match(0);
        var invalid = valid with { QueueId = queueId, Participants = [valid.Participants[0] with { Puuid = puuid }] };
        using var handler = new MatchHandler([valid])
        {
            Intercept = (request, _) => request.RequestUri!.AbsolutePath.EndsWith("/JP1_0") ? Response(ApiMatch(invalid)) : null
        };
        using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
        var error = await Assert.ThrowsAsync<RiotApiException>(() => new LolStatsProvider(fixture.Store, api).RefreshAnalysisAsync(account, Queues.Normal, 20, new InlineProgress(_ => { }), default));
        Assert.Contains("一致しません", error.Message);
        var cache = fixture.Store.Cache(account.Id); Assert.Empty(cache.Matches); Assert.Empty(cache.QueueUpdatedAt);
    }

    [Fact]
    public async Task RateLimitRetriesCanBeCancelledWithoutMarkingNormalFresh()
    {
        using var fixture = new StoreFixture(); var account = Account(); fixture.Store.Save(account);
        using var cts = new CancellationTokenSource();
        using var handler = new MatchHandler([])
        {
            Intercept = (_, _) =>
            {
                var response = Response(new { }, HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return response;
            }
        };
        using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LolStatsProvider(fixture.Store, api).RefreshAnalysisAsync(account, Queues.Normal, 20,
            new InlineProgress(message => { if (message.Contains("再試行")) cts.Cancel(); }), cts.Token));
        Assert.Single(handler.Requests); Assert.Empty(fixture.Store.Cache(account.Id).QueueUpdatedAt);
    }
}
