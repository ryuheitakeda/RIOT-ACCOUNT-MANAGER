using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using RiotAccounts.Core;
using Xunit;

// All OP.GG payloads here are synthetic; they mirror only the fields the provider reads.
public sealed class OpggTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(9));
    private static readonly Dictionary<int, string> Champions = new() { [103] = "Ahri", [64] = "LeeSin" };
    private static readonly InlineProgress Quiet = new(_ => { });
    private static RiotAccount Account(string? puuid = null) => new(Guid.NewGuid(), "Main", [new("lol", "Player", "JP1", "JP1", puuid)]);
    private static OpggApi Api(FakeOpgg handler) => new(new HttpClient(handler), TimeSpan.Zero);

    private sealed record Game(string Id, int QueueId, DateTimeOffset CreatedAt, bool Remake = false, int Kills = 5);

    private static object GamePayload(Game game) => new
    {
        id = game.Id, queue_id = game.QueueId, created_at = game.CreatedAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
        game_length_second = game.Remake ? 200 : 1800, is_remake = game.Remake,
        participants = new object[]
        {
            new { champion_id = 103, team_key = "BLUE", position = "MID", summoner = new { puuid = "opgg-self" },
                stats = new { kill = game.Kills, death = 2, assist = 7, minion_kill = 180, neutral_minion_kill = 12, vision_score = 20, result = "WIN" } },
            new { champion_id = 64, team_key = "RED", position = "JUNGLE", summoner = new { puuid = "opgg-enemy" },
                stats = new { kill = 3, death = 5, assist = 4, minion_kill = 30, neutral_minion_kill = 150, vision_score = 30, result = "LOSE" } }
        }
    };

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private sealed class FakeOpgg(List<Game> games) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; init; }
        public object[] SearchResults { get; init; } =
            [new { game_name = "player", tagline = "jp1", summoner_id = "sid-1", puuid = "opgg-self" }];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var uri = request.RequestUri!;
            if (Intercept?.Invoke(request) is { } intercepted) return Task.FromResult(intercepted);
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            if (uri.Host == OpggApi.ChampionHost)
                return Task.FromResult(Json(new { data = Champions.Select(c => new { id = c.Key, key = c.Value, name = "表示名" }) }));
            Assert.Equal(OpggApi.SummonerHost, uri.Host);
            if (uri.AbsolutePath == "/api/v3/jp/summoners")
            {
                Assert.Equal("Player#JP1", query["riot_id"]);
                return Task.FromResult(Json(new { data = SearchResults }));
            }
            if (uri.AbsolutePath == "/api/jp/summoners/sid-1/summary")
                return Task.FromResult(Json(new { data = new { summoner = new { league_stats = new object[]
                {
                    new { game_type = "SOLORANKED", win = 30, lose = 25, tier_info = new { tier = "GOLD", division = 2, lp = 45 } },
                    new { game_type = "FLEXRANKED", win = (int?)null, lose = (int?)null, tier_info = new { tier = (string?)null, division = (int?)null, lp = (int?)null } },
                    new { game_type = "ARENA", win = 1, lose = 1, tier_info = new { tier = "GOLD", division = 1, lp = 0 } }
                } } } }));
            Assert.Equal("/api/jp/summoners/sid-1/games", uri.AbsolutePath);
            Assert.Equal("20", query["limit"]);
            IEnumerable<Game> page = games.OrderByDescending(g => g.CreatedAt);
            page = query["game_type"] switch
            {
                "SOLORANKED" => page.Where(g => g.QueueId == 420),
                "FLEXRANKED" => page.Where(g => g.QueueId == 440),
                "TOTAL" => page,
                var other => throw new InvalidOperationException(other)
            };
            if (query.TryGetValue("ended_at", out var endedAt)) page = page.Where(g => g.CreatedAt < DateTimeOffset.Parse(endedAt, CultureInfo.InvariantCulture));
            return Task.FromResult(Json(new { data = page.Take(20).Select(GamePayload) }));
        }
    }

    [Fact]
    public void ParseGameMapsFieldsAndRewritesOwnPuuid()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(GamePayload(new("abc", 420, Now))));
        var match = OpggStatsProvider.ParseGame(json.RootElement, "opgg-self", "riot-self", Champions);
        Assert.Equal("OPGG_abc", match.Id);
        Assert.Equal(420, match.QueueId);
        Assert.Equal(Now, match.StartedAt);
        Assert.Equal(1800, match.DurationSeconds);
        Assert.False(match.Remake);
        var self = match.Participants.Single(p => p.Puuid == "riot-self");
        Assert.Equal(("Ahri", "MIDDLE", 100, 192, true), (self.Champion, self.Role, self.TeamId, self.Cs, self.Win));
        var enemy = match.Participants.Single(p => p.Puuid == "opgg-enemy");
        Assert.Equal(("LeeSin", "JUNGLE", 200, false), (enemy.Champion, enemy.Role, enemy.TeamId, enemy.Win));
    }

    [Fact]
    public void RegionsMapPlatforms()
    {
        Assert.Equal("jp", OpggRegions.Region("JP1"));
        Assert.Equal("eune", OpggRegions.Region("EUN1"));
        Assert.Equal("oce", OpggRegions.Region("oc1"));
        Assert.All(Regions.Routing.Keys, platform => OpggRegions.Region(platform));
        Assert.Throws<ArgumentException>(() => OpggRegions.Region("PBE1"));
    }

    [Fact]
    public async Task RanksAreMappedAndUnrankedQueuesSkipped()
    {
        using var fixture = new StoreFixture();
        var account = Account(); fixture.Store.Save(account);
        var handler = new FakeOpgg([]); using var api = Api(handler);
        await new OpggStatsProvider(fixture.Store, api).RefreshRanksAsync(account, default);
        var entry = Assert.Single(Assert.Single(fixture.Store.Cache(account.Id).Ranks).Entries);
        Assert.Equal(new RankEntry(Queues.Solo, "GOLD", "II", 45, 30, 25), entry);
        Assert.Equal("opgg-self", fixture.Store.Accounts().Single().Lol.Puuid);
        Assert.Equal(new OpggIdentity("jp", "Player", "JP1", "sid-1", "opgg-self"), OpggStatsProvider.Identity(fixture.Store, account.Id));
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("https", r.RequestUri!.Scheme);
            Assert.Contains("RiotAccounts", r.Headers.UserAgent.ToString());
        });
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    public async Task RankedAnalysisPagesByEndedAtAndSkipsRemakes(int count)
    {
        using var fixture = new StoreFixture();
        var account = Account(); fixture.Store.Save(account);
        var games = Enumerable.Range(0, 70).Select(i => new Game($"g{i}", i % 5 == 4 ? 440 : 420, Now.AddMinutes(-40 * i), Remake: i == 3)).ToList();
        var handler = new FakeOpgg(games); using var api = Api(handler);
        await new OpggStatsProvider(fixture.Store, api).RefreshAnalysisAsync(account, Queues.Solo, count, Quiet, default);
        var cache = fixture.Store.Cache(account.Id);
        var expected = games.Where(g => g.QueueId == 420 && !g.Remake).Take(count).Select(g => "OPGG_" + g.Id);
        Assert.Equal(expected, Analytics.Recent(cache, "opgg-self", Queues.Solo, count).Select(m => m.Id));
        Assert.DoesNotContain(cache.Matches, m => m.QueueId != 420);
        Assert.Empty(cache.Forecasts);
        Assert.Equal(cache.MatchesUpdatedAt, cache.QueueUpdatedAt[Queues.Solo]);
        var pages = handler.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/games"));
        Assert.Equal((int)Math.Ceiling((count + 1) / 20.0), pages);
        Assert.Single(handler.Requests, r => r.RequestUri!.Host == OpggApi.ChampionHost);
    }

    [Fact]
    public async Task NormalAnalysisFiltersAllGamesByQueue()
    {
        using var fixture = new StoreFixture();
        var account = Account(); fixture.Store.Save(account);
        var games = Enumerable.Range(0, 30).Select(i => new Game($"g{i}", i % 3 == 0 ? 420 : 400, Now.AddMinutes(-40 * i))).ToList();
        var handler = new FakeOpgg(games); using var api = Api(handler);
        await new OpggStatsProvider(fixture.Store, api).RefreshAnalysisAsync(account, Queues.Normal, 20, Quiet, default);
        var cache = fixture.Store.Cache(account.Id);
        Assert.Equal(20, Analytics.Recent(cache, "opgg-self", Queues.Normal, 20).Count);
        Assert.All(cache.Matches, m => Assert.Equal(400, m.QueueId));
        Assert.Contains(handler.Requests, r => r.RequestUri!.Query.Contains("game_type=TOTAL"));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/summary"));
    }

    [Fact]
    public async Task FuzzySearchResultIsNotAcceptedAsTheAccount()
    {
        using var fixture = new StoreFixture();
        var account = Account(); fixture.Store.Save(account);
        var handler = new FakeOpgg([]) { SearchResults = [new { game_name = "Player2", tagline = "JP1", summoner_id = "other", puuid = "other" }] };
        using var api = Api(handler);
        var error = await Assert.ThrowsAsync<RiotApiException>(() => new OpggStatsProvider(fixture.Store, api).RefreshRanksAsync(account, default));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Null(fixture.Store.Accounts().Single().Lol.Puuid);
        Assert.Null(OpggStatsProvider.Identity(fixture.Store, account.Id));
    }

    [Fact]
    public async Task RateLimitIsRetriedAndUnexpectedShapeIsReported()
    {
        using var fixture = new StoreFixture();
        var account = Account(); fixture.Store.Save(account);
        var limited = 0;
        var handler = new FakeOpgg([])
        {
            Intercept = r =>
            {
                if (!r.RequestUri!.AbsolutePath.EndsWith("/summary")) return null;
                if (limited++ == 0)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    response.Headers.RetryAfter = new(TimeSpan.FromSeconds(1));
                    return response;
                }
                return Json(new { data = new { unexpected = true } });
            }
        };
        using var api = Api(handler);
        var messages = new List<string>();
        var error = await Assert.ThrowsAsync<RiotApiException>(() =>
            new OpggStatsProvider(fixture.Store, api).RefreshRanksAsync(account, default, new InlineProgress(messages.Add)));
        Assert.Contains("応答形式", error.Message);
        Assert.Equal(2, limited);
        Assert.Contains(messages, m => m.Contains("取得制限"));
        Assert.Empty(fixture.Store.Cache(account.Id).Ranks);
    }

    [Fact]
    public async Task ServerErrorsAndForeignHostsFailClosed()
    {
        var handler = new FakeOpgg([]) { Intercept = r => new HttpResponseMessage(HttpStatusCode.Forbidden) };
        using var api = Api(handler);
        var error = await Assert.ThrowsAsync<RiotApiException>(() => api.GetAsync(OpggApi.SummonerHost, "/api/meta/seasons", default));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        await Assert.ThrowsAsync<ArgumentException>(() => api.GetAsync("example.com", "/api/x", default));
        await Assert.ThrowsAsync<ArgumentException>(() => api.GetAsync(OpggApi.SummonerHost, "/api/../x", default));
    }

    [Fact]
    public async Task RiotApiReplacesOpggPuuidWithoutDiscardingMatches()
    {
        using var fixture = new StoreFixture();
        var account = Account(); fixture.Store.Save(account);
        var games = Enumerable.Range(0, 5).Select(i => new Game($"g{i}", 420, Now.AddMinutes(-40 * i))).ToList();
        using var opgg = Api(new FakeOpgg(games));
        await new OpggStatsProvider(fixture.Store, opgg).RefreshAnalysisAsync(account, Queues.Solo, 20, Quiet, default);

        var riotHandler = new Handler((request, ct) => Task.FromResult(request.RequestUri!.AbsolutePath.Contains("/riot/account/")
            ? Json(new { puuid = "riot-self", gameName = "Player", tagLine = "JP1" })
            : Json(Array.Empty<object>())));
        using var riotClient = new HttpClient(riotHandler); using var riot = new RiotApi(riotClient, () => "test");
        await new LolStatsProvider(fixture.Store, riot).RefreshRanksAsync(fixture.Store.Accounts().Single(), default);

        Assert.Equal("riot-self", fixture.Store.Accounts().Single().Lol.Puuid);
        var cache = fixture.Store.Cache(account.Id);
        Assert.Equal(5, Analytics.Recent(cache, "riot-self", Queues.Solo, 20).Count);
        Assert.Equal(2, cache.Ranks.Count);

        // Later OP.GG refreshes key the account's matches by the Riot PUUID and reuse saved games.
        await new OpggStatsProvider(fixture.Store, opgg).RefreshAnalysisAsync(fixture.Store.Accounts().Single(), Queues.Solo, 20, Quiet, default);
        cache = fixture.Store.Cache(account.Id);
        Assert.Equal(5, cache.Matches.Count);
        Assert.Equal(5, Analytics.Recent(cache, "riot-self", Queues.Solo, 20).Count);
    }

    [Fact]
    public void RecentPrefersRiotCopyOfTheSameGame()
    {
        Participant Self(int kills) => new("self", 100, "Ahri", "MIDDLE", kills, 2, 7, 200, 20, true);
        var riot = new MatchRecord("JP1_1", 420, Now, 1800, false, [Self(5)]);
        var sameFromOpgg = new MatchRecord("OPGG_a", 420, Now.AddMinutes(-2), 1800, false, [Self(5)]);
        var otherFromOpgg = new MatchRecord("OPGG_b", 420, Now.AddMinutes(-4), 1800, false, [Self(9)]);
        var cache = new AccountCache { Matches = [riot, sameFromOpgg, otherFromOpgg] };
        Assert.Equal(new[] { "JP1_1", "OPGG_b" }, Analytics.Recent(cache, "self", Queues.Solo, 20).Select(m => m.Id));
    }
}
