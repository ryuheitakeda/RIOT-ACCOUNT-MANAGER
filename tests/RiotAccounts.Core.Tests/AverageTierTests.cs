using System.Net;
using System.Text;
using System.Text.Json;
using RiotAccounts.Core;
using Xunit;

// All matches, ranks and OP.GG payloads here are synthetic.
public sealed class AverageTierTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly List<(int Level, int Order)> NoCalibration = [];

    private static RankEntry Rank(int order, string queue = Queues.Solo)
    {
        var parts = RankOrder.Label(order).Split(' ');
        return new(queue, parts[0], parts.Length > 1 ? parts[1] : "I", 25, 10, 5);
    }

    // self + p1..p4 on blue, p5..p9 on red; levels 100, 110, ... 190.
    private static MatchRecord Lobby(string id = "JP1_1", DateTimeOffset? startedAt = null) => new(id, 400, startedAt ?? Now.AddHours(-1), 1800, false,
        [.. Enumerable.Range(0, 10).Select(i => new Participant(i == 0 ? "self" : $"p{i}", i < 5 ? 100 : 200, "Ahri", "MIDDLE", 1, 1, 1, 100, 10, i < 5, 100 + i * 10))]);

    // Both ranked queues are looked up together, so each player gets one observation per queue.
    private static IEnumerable<OpponentRankObservation> Observed(IEnumerable<int> players, Func<int, RankEntry?> rank, DateTimeOffset? at = null) =>
        players.SelectMany(i => Queues.Ranked.Select(q => new OpponentRankObservation($"p{i}", q, at ?? Now.AddMinutes(-5), rank(i) is { } r && r.QueueType == q ? r : null)));

    private static AccountCache Cache(IEnumerable<OpponentRankObservation> observed, RankSnapshot? own = null) => new()
    {
        Opponents = observed.ToList(),
        Ranks = own == null ? [] : [own]
    };

    [Fact]
    public void AveragesEveryParticipantIncludingSelfFromTheRankSnapshot()
    {
        var cache = Cache(Observed(Enumerable.Range(1, 9), _ => Rank(8)), new(Now.AddMinutes(-1), [Rank(12)]));
        var average = Analytics.AverageTier(Lobby(), "self", cache, Now, NoCalibration)!;
        Assert.Equal(8.4, average.Order, 6);
        Assert.Equal("SILVER IV", average.Label);
        Assert.Equal((MatchAverageTier.Riot, 10, 0, Now), (average.Source, average.KnownPlayers, average.EstimatedPlayers, average.ObservedAt));
    }

    [Fact]
    public void FlexStandsInForPlayersUnrankedInSoloDuo()
    {
        var cache = Cache(Observed(Enumerable.Range(1, 9), i => i == 1 ? Rank(20, Queues.Flex) : Rank(8)));
        var average = Analytics.AverageTier(Lobby(), "self", cache, Now, NoCalibration)!;
        Assert.Equal((20 + 8 * 8) / 9.0, average.Order, 6);
        Assert.Equal(9, average.KnownPlayers);
    }

    [Fact]
    public void TooFewKnownRanksRecordNothing()
    {
        Assert.Null(Analytics.AverageTier(Lobby(), "self", Cache(Observed(Enumerable.Range(1, 7), _ => Rank(8))), Now, NoCalibration));
        Assert.NotNull(Analytics.AverageTier(Lobby(), "self", Cache(Observed(Enumerable.Range(1, 8), _ => Rank(8))), Now, NoCalibration));
        // Observations and the own snapshot older than the TTL do not count.
        var stale = Cache(Observed(Enumerable.Range(1, 7), _ => Rank(8)).Concat(Observed([8], _ => Rank(8), Now - Analytics.OpponentRankTtl - TimeSpan.FromMinutes(1))),
            new(Now - Analytics.OpponentRankTtl - TimeSpan.FromMinutes(1), [Rank(12)]));
        Assert.Null(Analytics.AverageTier(Lobby(), "self", stale, Now, NoCalibration));
    }

    [Fact]
    public void UnrankedPlayersAreEstimatedFromTheirLevel()
    {
        var cache = Cache(Observed(Enumerable.Range(1, 9), i => i == 9 ? null : Rank(8)));
        // p9 is level 190; its five nearest calibration players have median order 16.
        List<(int Level, int Order)> calibration = [(185, 12), (188, 16), (190, 16), (195, 20), (200, 24), (30, 0)];
        var average = Analytics.AverageTier(Lobby(), "self", cache, Now, calibration)!;
        Assert.Equal((8 * 8 + 16) / 9.0, average.Order, 6);
        Assert.Equal((9, 1), (average.KnownPlayers, average.EstimatedPlayers));
        // Without enough calibration players the unranked player stays missing.
        Assert.Equal(8, Analytics.AverageTier(Lobby(), "self", cache, Now, NoCalibration)!.KnownPlayers);
    }

    [Theory]
    [InlineData("""{"tier":"SILVER","division":3}""", 9.0, "SILVER III")]
    [InlineData("""{"tier":"emerald","division":1}""", 23.0, "EMERALD I")]
    [InlineData("""{"tier":"MASTER","division":1}""", 28.0, "MASTER")]
    public void OpggAverageTierIsParsed(string info, double order, string label)
    {
        using var json = JsonDocument.Parse(OpggGame(info));
        var average = OpggStatsProvider.ParseGame(json.RootElement, "opgg-self", "self", new Dictionary<int, string>()).AverageTier!;
        Assert.Equal((order, label, MatchAverageTier.Opgg, Now), (average.Order, average.Label, average.Source, average.ObservedAt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("""{"tier":"","division":0}""")]
    public void MissingOpggAverageTierIsNull(string? info)
    {
        using var json = JsonDocument.Parse(OpggGame(info));
        Assert.Null(OpggStatsProvider.ParseGame(json.RootElement, "opgg-self", "self", new Dictionary<int, string>()).AverageTier);
    }

    private static string OpggGame(string? averageTierInfo) =>
        """{"id":"g1","queue_id":400,"created_at":"2026-09-09T12:00:00+00:00","game_length_second":1800,"is_remake":false,"participants":[]"""
        + (averageTierInfo == null ? "" : $",\"average_tier_info\":{averageTierInfo}") + "}";

    [Fact]
    public async Task NormalRefreshLooksUpAlliesAndRecordsTheAverageOnce()
    {
        using var fixture = new StoreFixture();
        var account = new RiotAccount(Guid.NewGuid(), "Main", [new("lol", "Riot Name", "JP1", "JP1", "self")]);
        fixture.Store.Save(account);
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        fixture.Store.SaveCache(account.Id, new() { Ranks = [new(DateTimeOffset.UtcNow, [Rank(12)])] });
        var lookedUp = new List<string>();
        var order = 8;
        using var handler = new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/by-puuid/", StringComparison.Ordinal) && path.Contains("/league/", StringComparison.Ordinal))
            {
                lookedUp.Add(path[(path.LastIndexOf('/') + 1)..]);
                return Task.FromResult(Json(new[] { Rank(order) }));
            }
            if (path.EndsWith("/ids", StringComparison.Ordinal))
                return Task.FromResult(Json(request.RequestUri.Query.Contains("queue=400", StringComparison.Ordinal) ? new[] { "JP1_1" } : []));
            if (path.EndsWith("/JP1_1", StringComparison.Ordinal))
                return Task.FromResult(Json(new
                {
                    metadata = new { matchId = "JP1_1" },
                    info = new
                    {
                        queueId = 400, gameStartTimestamp = startedAt.ToUnixTimeMilliseconds(), gameDuration = 1800,
                        participants = Lobby().Participants.Select(p => new { puuid = p.Puuid, teamId = p.TeamId, championName = p.Champion, teamPosition = p.Role,
                            kills = p.Kills, deaths = p.Deaths, assists = p.Assists, totalMinionsKilled = p.Cs, neutralMinionsKilled = 0, visionScore = p.VisionScore, win = p.Win, summonerLevel = p.SummonerLevel })
                    }
                }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var client = new HttpClient(handler);
        using var api = new RiotApi(client, () => "test");
        var provider = new LolStatsProvider(fixture.Store, api);
        await provider.RefreshAnalysisAsync(account, Queues.Normal, 20, new InlineProgress(_ => { }), default);

        // Allies and enemies are looked up; the account's own rank is not (it comes from the saved snapshot).
        Assert.Equal(Enumerable.Range(1, 9).Select(i => $"p{i}").Order(), lookedUp.Order());
        var average = Assert.Single(fixture.Store.Cache(account.Id).Matches).AverageTier!;
        Assert.Equal(8.4, average.Order, 6);
        Assert.Equal(10, average.KnownPlayers);
        Assert.Equal(1, fixture.Store.Cache(account.Id).Forecasts.Single().MatchCount);

        // A recorded average is an observation: later lookups with different ranks do not rewrite it.
        var cache = fixture.Store.Cache(account.Id);
        cache.Opponents.Clear();
        fixture.Store.SaveCache(account.Id, cache);
        order = 20;
        await provider.RefreshAnalysisAsync(account, Queues.Normal, 20, new InlineProgress(_ => { }), default);
        Assert.Equal(18, lookedUp.Count);
        Assert.Equal(average, Assert.Single(fixture.Store.Cache(account.Id).Matches).AverageTier);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };
}
