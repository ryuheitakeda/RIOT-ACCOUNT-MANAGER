using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RiotAccounts.Core;

using Xunit;

public sealed class CoreTests
{
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, actual {actual}."); }
static void True(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
static void Near(double expected, double actual) => True(Math.Abs(expected - actual) < .000001, $"Expected {expected}, actual {actual}");
static async Task<T> Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T error) { return error; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static DateTimeOffset Now() => new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
static RankEntry Rank(int order = 12, string queue = Queues.Solo)
{
    var parts = RankOrder.Label(order).Split(' ');
    return new(queue, parts[0], parts.Length > 1 ? parts[1] : "I", 25, 10, 5);
}
static MatchRecord Match(int index, string queue = Queues.Solo, DateTimeOffset? started = null) => new(
    $"JP1_{index}", Queues.Id(queue), started ?? Now().AddHours(-index), 1800, false,
    [new("self", 100, "Ahri", "MIDDLE", 5, 2, 7, 200, 20, true),
     .. Enumerable.Range(0, 5).Select(i => new Participant($"enemy{index}-{i}", 200, "Garen", "TOP", 2, 5, 3, 150, 15, false))]);
static AccountCache Sample(int games = 10, int known = 50)
{
    var cache = new AccountCache { Matches = Enumerable.Range(0, games).Select(i => Match(i)).ToList() };
    cache.Opponents = cache.Matches.SelectMany(m => m.Participants.Skip(1)).Take(known)
        .Select(p => new OpponentRankObservation(p.Puuid, Queues.Solo, Now().AddMinutes(-10), Rank())).ToList();
    return cache;
}
static RiotAccount Account(string? puuid = "self") => new(Guid.NewGuid(), "Main", [new("lol", "Riot Name", "JP1", "JP1", puuid)]);
static HttpResponseMessage Response(HttpStatusCode code, object? body = null)
{
    var response = new HttpResponseMessage(code);
    if (body != null) response.Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");
    return response;
}
static object ApiMatch(int index, DateTimeOffset? started = null) => new
{
    metadata = new { matchId = $"JP1_{index}" },
    info = new { queueId = 420, gameStartTimestamp = (started ?? DateTimeOffset.UtcNow.AddHours(-1)).ToUnixTimeMilliseconds(), gameDuration = 1800,
        participants = Match(index).Participants.Select(p => new { puuid = p.Puuid, teamId = p.TeamId, championName = p.Champion,
            teamPosition = p.Role, kills = p.Kills, deaths = p.Deaths, assists = p.Assists, totalMinionsKilled = 180,
            neutralMinionsKilled = 20, visionScore = p.VisionScore, win = p.Win }) }
};

[Fact]
public void RankCategories()
{
    for (var order = 0; order <= 30; order++) Equal<int?>(order, Rank(order).Order);
    Equal("EMERALD IV", RankOrder.Label(20)); Equal("DIAMOND I", RankOrder.Label(27));
    Equal<int?>(null, RankOrder.Parse("UNRANKED", "I")); Equal<int?>(null, RankOrder.Parse("GOLD", "V"));
    Equal("sea", Regions.Regional("sg2")); Equal("asia", Regions.AccountRegional("SG2"));
}
[Fact]
public void Quantiles()
{
    var cache = Sample();
    cache.Opponents = cache.Opponents.Select((o, i) => o with { Rank = Rank(i % 31) }).ToList();
    var prediction = Analytics.Predict(cache, "self", Queues.Solo, Now());
    Equal("BRONZE II", prediction.Lower); Equal("GOLD IV", prediction.Median); Equal("PLATINUM II", prediction.Upper);
    Equal<DateTimeOffset?>(Now().AddMinutes(-10), prediction.OldestRankObservedAt);
}
[Fact]
public void Threshold()
{
    var prediction = Analytics.Predict(Sample(10, 40), "self", Queues.Solo, Now());
    Equal(10, prediction.MatchCount); Equal(50, prediction.TotalPlayers); Equal(40, prediction.KnownPlayers);
    Equal("GOLD IV", prediction.Median); Near(.2, prediction.MissingRate);
}
[Fact]
public void Insufficient()
{
    Equal<string?>(null, Analytics.Predict(Sample(9, 45), "self", Queues.Solo, Now()).Median);
    Equal<string?>(null, Analytics.Predict(Sample(10, 39), "self", Queues.Solo, Now()).Median);
    Equal(1d, Analytics.Predict(new(), "self", Queues.Solo, Now()).MissingRate);
}
[Fact]
public void ForecastWindow()
{
    var cache = Sample(25, 125);
    cache.Matches.Add(Match(50, Queues.Flex));
    cache.Matches.Add(Match(51, started: Now().AddDays(-31)));
    cache.Matches.Add(Match(52, started: Now().AddDays(1)));
    cache.Matches.Add(Match(53) with { Remake = true });
    cache.Matches.Add(cache.Matches[0]);
    var prediction = Analytics.Predict(cache, "self", Queues.Solo, Now());
    Equal(10, prediction.MatchCount); Equal(50, prediction.TotalPlayers); Equal(50, prediction.KnownPlayers);
    Equal(1, Analytics.Predict(cache, "self", Queues.Flex, Now()).MatchCount);
    Equal(0, Analytics.Predict(cache, "unknown", Queues.Solo, Now()).MatchCount);
}
[Fact]
public void Observations()
{
    var cache = Sample();
    var first = cache.Opponents[0];
    cache.Opponents.Add(first with { ObservedAt = Now(), Rank = null });
    cache.Opponents[1] = cache.Opponents[1] with { ObservedAt = Now() - Analytics.OpponentRankTtl - TimeSpan.FromMinutes(1) };
    cache.Opponents[2] = cache.Opponents[2] with { Rank = Rank(queue: Queues.Flex) };
    cache.Opponents[3] = cache.Opponents[3] with { ObservedAt = Now().AddMinutes(1) };
    cache.Opponents[4] = cache.Opponents[4] with { ObservedAt = Now() - Analytics.OpponentRankTtl + TimeSpan.FromMinutes(1) };
    Equal(46, Analytics.Predict(cache, "self", Queues.Solo, Now()).KnownPlayers);
}
[Fact]
public void MissingOpponents()
{
    var cache = Sample();
    cache.Matches[0] = cache.Matches[0] with { Participants = [cache.Matches[0].Participants[0], cache.Matches[0].Participants[1], cache.Matches[0].Participants[1]] };
    var forecast = Analytics.Predict(cache, "self", Queues.Solo, Now());
    Equal(50, forecast.TotalPlayers); Equal(46, forecast.KnownPlayers);
}
[Fact]
public void Performance()
{
    var first = Match(0);
    var second = Match(1) with { DurationSeconds = 600, Participants = [new("self", 100, "Ahri", "MIDDLE", 3, 0, 2, 100, 10, false)] };
    var summary = Analytics.Summarize([first, second, first, first with { Id = "remake", Remake = true }], "self", p => p.Champion).Single();
    Equal(2, summary.Games); Equal(1, summary.Wins); Near(8.5, summary.Kda); Near(7.5, summary.CsPerMinute); Near(15, summary.VisionPerGame);
}
[Fact]
public void Persistence()
{
    using var fixture = new StoreFixture(); var account = Account();
    account.Profiles.Add(new("tft", "Other", "EUW", "EUW1", "tft-player"));
    fixture.Store.Save(account, new("login-private", "password-private-123"));
    fixture.Store.SetSecret("api-key", "RGAPI-private-value");
    var cache = Sample(); cache.Ranks.Add(new(Now(), [Rank()])); cache.Forecasts.Add(Analytics.Predict(cache, "self", Queues.Solo, Now()));
    cache.QueueUpdatedAt[Queues.Solo] = Now(); fixture.Store.SaveCache(account.Id, cache);
    var restarted = new Store(fixture.File, fixture.Protector);
    Equal(2, restarted.Accounts().Single().Profiles.Count); Equal("password-private-123", restarted.Credentials(account.Id).Password);
    Equal("RGAPI-private-value", restarted.GetSecret("api-key")); Equal(10, restarted.Cache(account.Id).Matches.Count);
    Equal(Now(), restarted.Cache(account.Id).QueueUpdatedAt[Queues.Solo]);
    using var c = fixture.Open(); using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM game_profiles"; Equal(2L, (long)cmd.ExecuteScalar()!);
    cmd.CommandText = "SELECT COUNT(DISTINCT kind) FROM game_records"; Equal(4L, (long)cmd.ExecuteScalar()!);
    foreach (var file in Directory.EnumerateFiles(fixture.Directory))
    {
        var content = Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(file));
        True(!content.Contains("password-private-123") && !content.Contains("login-private") && !content.Contains("RGAPI-private-value"), "Secret plaintext persisted.");
    }
}
[Fact]
public void EditIdentity()
{
    using var fixture = new StoreFixture(); var account = Account();
    fixture.Store.Save(account, new("login", "pass")); fixture.Store.SaveCache(account.Id, Sample());
    fixture.Store.Save(account with { Label = "New label" }, new("login2", "pass2"));
    Equal(10, fixture.Store.Cache(account.Id).Matches.Count);
    fixture.Store.Save(account with { Profiles = [account.Lol with { GameName = "Different" }] });
    Equal(0, fixture.Store.Cache(account.Id).Matches.Count); Equal<string?>(null, fixture.Store.Accounts().Single().Lol.Puuid);
    Equal("pass2", fixture.Store.Credentials(account.Id).Password);
}
[Fact]
public void EncryptionFailure()
{
    using var fixture = new StoreFixture(); var account = Account(); fixture.Store.Save(account, new("login", "pass"));
    fixture.Protector.Fail = true;
    try { fixture.Store.Save(account with { Label = "should-not-save" }, new("new", "new")); throw new Exception("Expected encryption failure"); }
    catch (CryptographicException) { }
    Equal("Main", fixture.Store.Accounts().Single().Label); Equal("pass", fixture.Store.Credentials(account.Id).Password);
}
[Fact]
public void DeleteAccount()
{
    using var fixture = new StoreFixture(); var account = Account(); var other = Account();
    fixture.Store.Save(account, new("login", "pass")); fixture.Store.SaveCache(account.Id, Sample()); fixture.Store.Save(other);
    fixture.Store.SetSecret("api-key", "private"); fixture.Store.Delete(account.Id);
    Equal(other.Id, fixture.Store.Accounts().Single().Id); Equal(0, fixture.Store.Cache(account.Id).Matches.Count);
    Equal<string?>(null, fixture.Store.GetSecret(account.Id.ToString())); Equal("private", fixture.Store.GetSecret("api-key"));
}
[Fact]
public void LegacyMigration()
{
    using var fixture = new StoreFixture(); var account = Account();
    fixture.Store.Write("account", account.Id.ToString(), account);
    using (var c = fixture.Open())
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO documents VALUES('cache',$id,$json)";
        cmd.Parameters.AddWithValue("$id", account.Id.ToString()); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(Sample(), new JsonSerializerOptions(JsonSerializerDefaults.Web))); cmd.ExecuteNonQuery();
    }
    Equal("self", fixture.Store.Accounts().Single().Lol.Puuid); Equal(10, fixture.Store.Cache(account.Id).Matches.Count);
    fixture.Store.Save(account); fixture.Store.SaveCache(account.Id, fixture.Store.Cache(account.Id));
    Equal(10, fixture.Store.Cache(account.Id).Matches.Count);
}
[Fact]
public async Task InvalidKey()
{
    foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(status))); using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "private-token");
        var error = await Throws<RiotApiException>(() => api.GetAsync<List<RankEntry>>("jp1", "/lol/test", default));
        Equal(status, error.StatusCode); Equal(1, handler.Count); True(!error.Message.Contains("private-token"));
    }
}
[Fact]
public async Task RetryAfter()
{
    var n = 0;
    using var handler = new Handler((_, _) =>
    {
        var response = Response(++n == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, new[] { "done" });
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1)); return Task.FromResult(response);
    });
    using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
    var timer = Stopwatch.StartNew(); var result = await api.GetAsync<string[]>("jp1", "/lol/test", default);
    Equal("done", result.Single()); Equal(2, handler.Count); True(timer.Elapsed >= TimeSpan.FromSeconds(.95));
}
[Fact]
public async Task CancelRateLimit()
{
    using var first = new CancellationTokenSource();
    using var handler = new Handler((_, _) =>
    {
        var response = Response(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60)); return Task.FromResult(response);
    });
    using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
    await Throws<OperationCanceledException>(() => api.GetAsync<string[]>("jp1", "/lol/test", first.Token, new InlineProgress(_ => first.Cancel())));
    using var second = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
    await Throws<OperationCanceledException>(() => api.GetAsync<string[]>("jp1", "/lol/test", second.Token));
    Equal(1, handler.Count);
}
[Fact]
public async Task CancelGate()
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var first = new CancellationTokenSource();
    using var handler = new Handler(async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Response(HttpStatusCode.OK); });
    using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
    var active = api.GetAsync<string[]>("jp1", "/lol/test", first.Token); await entered.Task;
    using var second = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
    await Throws<OperationCanceledException>(() => api.GetAsync<string[]>("jp1", "/lol/test", second.Token));
    first.Cancel(); await Throws<OperationCanceledException>(() => active); Equal(1, handler.Count);
}
[Fact]
public async Task BurstLimit()
{
    using var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, Array.Empty<string>())));
    using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
    var timer = Stopwatch.StartNew();
    for (var i = 0; i < 21; i++) await api.GetAsync<string[]>("jp1", "/lol/test", default);
    True(timer.Elapsed >= TimeSpan.FromSeconds(1)); Equal(21, handler.Count);
}
[Fact]
public async Task BadResponses()
{
    foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.InternalServerError })
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(status))); using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
        Equal(status, (await Throws<RiotApiException>(() => api.GetAsync<string[]>("jp1", "/lol/test", default))).StatusCode); Equal(1, handler.Count);
    }
    using var badHandler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>error</html>") }));
    using var badClient = new HttpClient(badHandler); using var badApi = new RiotApi(badClient, () => "test");
    await Throws<RiotApiException>(() => badApi.GetAsync<string[]>("jp1", "/lol/test", default));
}
[Fact]
public async Task RejectHost()
{
    using var handler = new Handler((_, _) => throw new Exception("Must not send")); using var client = new HttpClient(handler);
    using var api = new RiotApi(client, () => throw new Exception("Must not read key"));
    await Throws<ArgumentException>(() => api.GetAsync<string[]>("attacker.example/path?", "/lol/test", default)); Equal(0, handler.Count);
}
[Fact]
public async Task PreserveRanks()
{
    using var fixture = new StoreFixture(); var account = Account(); fixture.Store.Save(account);
    var cache = Sample(); cache.Ranks.Add(new(Now(), [Rank()])); fixture.Store.SaveCache(account.Id, cache);
    using var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.Forbidden))); using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
    await Throws<RiotApiException>(() => new LolStatsProvider(fixture.Store, api).RefreshRanksAsync(account, default));
    Equal(1, fixture.Store.Cache(account.Id).Ranks.Count); Equal(10, fixture.Store.Cache(account.Id).Matches.Count);
}
[Fact]
public async Task ResolveIdentity()
{
    using var fixture = new StoreFixture(); var account = Account(null) with { Profiles = [new("lol", "A Name /漢字", "TAG", "SG2"), new("tft", "TFT", "TAG", "JP1", "other")] };
    fixture.Store.Save(account);
    using var handler = new Handler((request, _) =>
    {
        True(request.Headers.Contains("X-Riot-Token")); True(!request.RequestUri!.Query.Contains("test-key"));
        if (request.RequestUri.AbsolutePath.StartsWith("/riot/account"))
        {
            Equal("asia.api.riotgames.com", request.RequestUri.Host); True(request.RequestUri.AbsoluteUri.Contains("A%20Name%20%2F"));
            return Task.FromResult(Response(HttpStatusCode.OK, new { puuid = "resolved", gameName = "A NAME /漢字", tagLine = "TAG" }));
        }
        Equal("sg2.api.riotgames.com", request.RequestUri.Host); True(request.RequestUri.AbsolutePath.EndsWith("/by-puuid/resolved"));
        return Task.FromResult(Response(HttpStatusCode.OK, Array.Empty<RankEntry>()));
    });
    using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test-key");
    await new LolStatsProvider(fixture.Store, api).RefreshRanksAsync(account, default);
    var saved = fixture.Store.Accounts().Single(); Equal(2, saved.Profiles.Count); Equal("resolved", saved.Lol.Puuid); Equal(1, fixture.Store.Cache(account.Id).Ranks.Count);
}
[Fact]
public async Task StalePuuidRenewed()
{
    using var fixture = new StoreFixture(); var account = Account(); fixture.Store.Save(account);
    var cache = Sample(); cache.Ranks.Add(new(Now(), [Rank()])); cache.QueueUpdatedAt[Queues.Solo] = Now(); fixture.Store.SaveCache(account.Id, cache);
    using var handler = new Handler((request, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/by-puuid/self")) return Task.FromResult(Response(HttpStatusCode.BadRequest));
        if (path.StartsWith("/riot/account")) return Task.FromResult(Response(HttpStatusCode.OK, new { puuid = "renewed", gameName = "Riot Name", tagLine = "JP1" }));
        if (path.EndsWith("/by-puuid/renewed")) return Task.FromResult(Response(HttpStatusCode.OK, new[] { Rank() }));
        return Task.FromResult(Response(HttpStatusCode.NotFound));
    });
    using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
    await new LolStatsProvider(fixture.Store, api).RefreshRanksAsync(account, default);
    Equal("renewed", fixture.Store.Accounts().Single().Lol.Puuid);
    var saved = fixture.Store.Cache(account.Id);
    Equal(2, saved.Ranks.Count); Equal(0, saved.Matches.Count); Equal(0, saved.Opponents.Count); Equal(0, saved.QueueUpdatedAt.Count);
}
[Fact]
public async Task BadRequestWithCurrentPuuid()
{
    using var fixture = new StoreFixture(); var account = Account(); fixture.Store.Save(account);
    fixture.Store.SaveCache(account.Id, Sample());
    var calls = 0;
    using var handler = new Handler((request, _) =>
    {
        calls++;
        if (request.RequestUri!.AbsolutePath.StartsWith("/riot/account")) return Task.FromResult(Response(HttpStatusCode.OK, new { puuid = "self", gameName = "Riot Name", tagLine = "JP1" }));
        return Task.FromResult(Response(HttpStatusCode.BadRequest));
    });
    using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
    var error = await Throws<RiotApiException>(() => new LolStatsProvider(fixture.Store, api).RefreshRanksAsync(account, default));
    Equal(HttpStatusCode.BadRequest, error.StatusCode); Equal(2, calls);
    Equal("self", fixture.Store.Accounts().Single().Lol.Puuid); Equal(10, fixture.Store.Cache(account.Id).Matches.Count);
}
[Fact]
public async Task StalePuuidRenewedForNormalMatches()
{
    using var fixture = new StoreFixture(); var account = Account(); fixture.Store.Save(account);
    fixture.Store.SaveCache(account.Id, Sample());
    using var handler = new Handler((request, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.Contains("/by-puuid/self/")) return Task.FromResult(Response(HttpStatusCode.BadRequest));
        if (path.StartsWith("/riot/account")) return Task.FromResult(Response(HttpStatusCode.OK, new { puuid = "renewed", gameName = "Riot Name", tagLine = "JP1" }));
        if (path.Contains("/by-puuid/renewed/")) return Task.FromResult(Response(HttpStatusCode.OK, Array.Empty<string>()));
        return Task.FromResult(Response(HttpStatusCode.NotFound));
    });
    using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
    await new LolStatsProvider(fixture.Store, api).RefreshAnalysisAsync(account, Queues.Normal, 20, new InlineProgress(_ => { }), default);
    Equal("renewed", fixture.Store.Accounts().Single().Lol.Puuid);
    var saved = fixture.Store.Cache(account.Id); Equal(0, saved.Matches.Count); True(saved.QueueUpdatedAt.ContainsKey(Queues.Normal));
}
[Fact]
public async Task MissingOpponentApi()
{
    using var fixture = new StoreFixture(); var account = Account(); fixture.Store.Save(account);
    using var handler = new Handler((request, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/by-puuid/self")) return Task.FromResult(Response(HttpStatusCode.OK, new[] { Rank(), Rank(queue: Queues.Flex) }));
        if (path.EndsWith("/ids")) { True(request.RequestUri.Query.Contains("queue=420")); return Task.FromResult(Response(HttpStatusCode.OK, new[] { "JP1_0" })); }
        if (path.EndsWith("/JP1_0")) return Task.FromResult(Response(HttpStatusCode.OK, ApiMatch(0)));
        return Task.FromResult(Response(HttpStatusCode.NotFound));
    });
    using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
    await new LolStatsProvider(fixture.Store, api).RefreshAnalysisAsync(account, Queues.Solo, 20, new InlineProgress(_ => { }), default);
    var cache = fixture.Store.Cache(account.Id); Equal(10, cache.Opponents.Count); Equal(0, cache.Forecasts.Single().KnownPlayers);
    foreach (var queue in Queues.Ranked) Equal(5, cache.Opponents.Count(o => o.QueueType == queue && o.Rank == null));
    Equal(1, cache.QueueUpdatedAt.Count); True(cache.QueueUpdatedAt.ContainsKey(Queues.Solo));
    Equal(2, cache.Ranks.Single().Entries.Count);
}
[Fact]
public async Task OpponentRanksSharedAcrossQueues()
{
    using var fixture = new StoreFixture(); var account = Account(); fixture.Store.Save(account);
    var enemyLookups = 0;
    static object ApiMatchIn(string id, int queueId) => new
    {
        metadata = new { matchId = id },
        info = new { queueId, gameStartTimestamp = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds(), gameDuration = 1800,
            participants = Match(0).Participants.Select(p => new { puuid = p.Puuid, teamId = p.TeamId, championName = p.Champion, teamPosition = p.Role,
                kills = p.Kills, deaths = p.Deaths, assists = p.Assists, totalMinionsKilled = 180, neutralMinionsKilled = 20, visionScore = p.VisionScore, win = p.Win }) }
    };
    using var handler = new Handler((request, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/by-puuid/self")) return Task.FromResult(Response(HttpStatusCode.OK, new[] { Rank(), Rank(queue: Queues.Flex) }));
        if (path.Contains("/by-puuid/enemy")) { enemyLookups++; return Task.FromResult(Response(HttpStatusCode.OK, new[] { Rank(10), Rank(20, Queues.Flex) })); }
        if (path.EndsWith("/ids")) return Task.FromResult(Response(HttpStatusCode.OK, new[] { request.RequestUri.Query.Contains("queue=440") ? "JP1_1" : "JP1_0" }));
        if (path.EndsWith("/JP1_0")) return Task.FromResult(Response(HttpStatusCode.OK, ApiMatchIn("JP1_0", 420)));
        if (path.EndsWith("/JP1_1")) return Task.FromResult(Response(HttpStatusCode.OK, ApiMatchIn("JP1_1", 440)));
        return Task.FromResult(Response(HttpStatusCode.NotFound));
    });
    using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
    var provider = new LolStatsProvider(fixture.Store, api);
    await provider.RefreshAnalysisAsync(account, Queues.Solo, 20, new InlineProgress(_ => { }), default);
    Equal(5, enemyLookups);
    var cache = fixture.Store.Cache(account.Id); Equal(10, cache.Opponents.Count);
    foreach (var queue in Queues.Ranked) Equal(5, cache.Opponents.Count(o => o.QueueType == queue && o.Rank?.QueueType == queue));
    // The Flex refresh meets the same five opponents and reuses the Solo refresh's lookups.
    await provider.RefreshAnalysisAsync(account, Queues.Flex, 20, new InlineProgress(_ => { }), default);
    Equal(5, enemyLookups);
    cache = fixture.Store.Cache(account.Id); Equal(10, cache.Opponents.Count);
    var flex = cache.Forecasts.Single(f => f.QueueType == Queues.Flex); Equal(1, flex.MatchCount); Equal(5, flex.KnownPlayers); Equal(5, flex.TotalPlayers);
    // Past the retention period the ranks are looked up again.
    cache.Opponents = cache.Opponents.Select(o => o with { ObservedAt = o.ObservedAt - Analytics.OpponentRankTtl - TimeSpan.FromMinutes(1) }).ToList();
    fixture.Store.SaveCache(account.Id, cache);
    await provider.RefreshAnalysisAsync(account, Queues.Flex, 20, new InlineProgress(_ => { }), default);
    Equal(10, enemyLookups); Equal(20, fixture.Store.Cache(account.Id).Opponents.Count);
}
[Fact]
public async Task PartialCancellation()
{
    using var fixture = new StoreFixture(); var account = Account(); fixture.Store.Save(account);
    var oldCache = new AccountCache { Matches = [Match(99)], MatchesUpdatedAt = Now() }; fixture.Store.SaveCache(account.Id, oldCache);
    using var cts = new CancellationTokenSource();
    using var handler = new Handler((request, ct) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/by-puuid/self")) return Task.FromResult(Response(HttpStatusCode.OK, new[] { Rank() }));
        if (path.EndsWith("/ids")) return Task.FromResult(Response(HttpStatusCode.OK, new[] { "JP1_0", "JP1_1" }));
        if (path.EndsWith("/JP1_1")) { cts.Cancel(); ct.ThrowIfCancellationRequested(); }
        return Task.FromResult(Response(HttpStatusCode.OK, ApiMatch(0)));
    });
    using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
    await Throws<OperationCanceledException>(() => new LolStatsProvider(fixture.Store, api).RefreshAnalysisAsync(account, Queues.Solo, 20, new InlineProgress(_ => { }), cts.Token));
    var cache = fixture.Store.Cache(account.Id); Equal(2, cache.Matches.Count); Equal<DateTimeOffset?>(Now(), cache.MatchesUpdatedAt); Equal(0, cache.Forecasts.Count);
}
[Fact]
public async Task InvalidQueue()
{
    using var fixture = new StoreFixture(); var account = Account(); fixture.Store.Save(account);
    using var handler = new Handler((_, _) => throw new Exception("Must not call")); using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
    await Throws<ArgumentException>(() => new LolStatsProvider(fixture.Store, api).RefreshAnalysisAsync(account, "unknown", 20, new InlineProgress(_ => { }), default));
    Equal(0, handler.Count); Equal(0, fixture.Store.Cache(account.Id).Ranks.Count);
}
[Fact]
public async Task MalformedMatchPreservesCache()
{
    using var fixture = new StoreFixture(); var account = Account(); fixture.Store.Save(account);
    fixture.Store.SaveCache(account.Id, Sample());
    using var handler = new Handler((request, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/by-puuid/self")) return Task.FromResult(Response(HttpStatusCode.OK, new[] { Rank() }));
        if (path.EndsWith("/ids")) return Task.FromResult(Response(HttpStatusCode.OK, new[] { "JP1_100" }));
        return Task.FromResult(Response(HttpStatusCode.OK, new { metadata = new { matchId = "JP1_100" }, info = new { } }));
    });
    using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
    var error = await Throws<RiotApiException>(() => new LolStatsProvider(fixture.Store, api).RefreshAnalysisAsync(account, Queues.Solo, 20, new InlineProgress(_ => { }), default));
    True(error.Message.Contains("形式が不正")); Equal(10, fixture.Store.Cache(account.Id).Matches.Count);
}
[Fact]
public async Task NetworkFailurePreservesCache()
{
    using var fixture = new StoreFixture(); var account = Account(); fixture.Store.Save(account);
    var cache = Sample(); cache.Ranks.Add(new(Now(), [Rank()])); fixture.Store.SaveCache(account.Id, cache);
    using var handler = new Handler((_, _) => throw new HttpRequestException("Offline"));
    using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");
    await Throws<HttpRequestException>(() => new LolStatsProvider(fixture.Store, api).RefreshRanksAsync(account, default));
    Equal(1, fixture.Store.Cache(account.Id).Ranks.Count); Equal(10, fixture.Store.Cache(account.Id).Matches.Count);
}
[Fact]
public void PlatformEditInvalidatesResolvedIdentity()
{
    using var fixture = new StoreFixture(); var account = Account(); fixture.Store.Save(account);
    fixture.Store.SaveCache(account.Id, Sample());
    fixture.Store.Save(account with { Profiles = [account.Lol with { Platform = "NA1" }] });
    Equal<string?>(null, fixture.Store.Accounts().Single().Lol.Puuid); Equal(0, fixture.Store.Cache(account.Id).Matches.Count);
}
[Fact]
public void ParseMatch()
{
    using var json = JsonDocument.Parse("""
    {"metadata":{"matchId":"JP1_123"},"info":{"queueId":420,"gameStartTimestamp":1000,"gameDuration":180,"participants":[{"puuid":"self","teamId":100,"gameEndedInEarlySurrender":true,"totalMinionsKilled":20,"neutralMinionsKilled":4}]}}
    """);
    var result = LolStatsProvider.ParseMatch(json.RootElement); True(result.Remake); Equal(24, result.Participants.Single().Cs); Equal(0, result.Participants.Single().Kills); Equal(180, result.DurationSeconds);
}

}

sealed class InlineProgress(Action<string> action) : IProgress<string> { public void Report(string value) => action(value); }
sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    public int Count { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Count++; return send(request, cancellationToken); }
}
// Test-only protector: production uses Windows DPAPI. AES lets portable tests inspect SQLite for plaintext leakage.
sealed class TestProtector : ISecretProtector
{
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    public bool Fail { get; set; }
    public byte[] Protect(byte[] data)
    {
        if (Fail) throw new CryptographicException("Test encryption failure");
        var result = new byte[28 + data.Length]; RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16); aes.Encrypt(result.AsSpan(0, 12), data, result.AsSpan(28), result.AsSpan(12, 16)); return result;
    }
    public byte[] Unprotect(byte[] data)
    {
        var result = new byte[data.Length - 28]; using var aes = new AesGcm(key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), result); return result;
    }
}
sealed class StoreFixture : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "riot-core-tests-" + Guid.NewGuid());
    public string File => Path.Combine(Directory, "accounts.db");
    public TestProtector Protector { get; } = new();
    public Store Store { get; }
    public StoreFixture() { Store = new(File, Protector); }
    public SqliteConnection Open() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = File, Pooling = false }.ToString()); c.Open(); return c; }
    public void Dispose() => System.IO.Directory.Delete(Directory, true);
}
