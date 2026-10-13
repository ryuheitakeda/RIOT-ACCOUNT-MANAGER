using System.Net;
using System.Text.Json;
using RiotAccounts.Core;
using Xunit;
using static WebTestData;

// Importing the web version's data merges it into the local store (keeping local-only records) and never calls Riot.
public sealed class WebImportTests
{
    static WebStatsProvider Provider(StoreFixture fixture, WebTestServer server, DiagnosticLog? log = null) =>
        new(fixture.Store, new WebStatsClient(server.Http, () => "https://web.example:8446/"), TimeSpan.Zero, log);

    static RankSnapshot Snapshot(DateTimeOffset at, int lp = 40) => new(at, [new(Queues.Solo, "GOLD", "II", lp, 10, 8)]);
    static OpponentRankObservation Opponent(string puuid, DateTimeOffset at, string queue = Queues.Solo) => new(puuid, queue, at, new(queue, "GOLD", "IV", 10, 5, 5));
    static Forecast Forecast(DateTimeOffset at) => new(Queues.Solo, at, 10, 50, 50, "GOLD IV", "GOLD II", "PLATINUM IV", "説明");
    static Participant Player(string puuid, bool self, int level = 0) =>
        self ? new(puuid, 100, "Ahri", "MIDDLE", 1, 2, 3, 100, 10, true, level) : new(puuid, 200, "Garen", "TOP", 2, 5, 3, 150, 15, false, level);
    static object ApiMatch(string id, string self, DateTimeOffset started) => new
    {
        metadata = new { matchId = id },
        info = new
        {
            queueId = 420, gameStartTimestamp = started.ToUnixTimeMilliseconds(), gameDuration = 1800,
            participants = new[] { Player(self, true, 300), Player("enemy", false, 200) }.Select(p => new
            {
                puuid = p.Puuid, teamId = p.TeamId, championName = p.Champion, teamPosition = p.Role, kills = p.Kills, deaths = p.Deaths, assists = p.Assists,
                totalMinionsKilled = p.Cs, neutralMinionsKilled = 0, visionScore = p.VisionScore, win = p.Win, summonerLevel = p.SummonerLevel
            })
        }
    };

    // Every saved row of the account's LoL records, profile and cache document, for "nothing was written" checks.
    static List<string> Rows(StoreFixture fixture, Guid id)
    {
        using var c = fixture.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT kind||'|'||record_id||'|'||json FROM game_records WHERE account_id=$id ORDER BY kind,record_id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        var rows = new List<string>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) rows.Add(reader.GetString(0));
        cmd.CommandText = "SELECT json FROM game_profiles WHERE account_id=$id AND game='lol'";
        rows.Add((string)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT json FROM documents WHERE kind='cache' AND id=$id";
        rows.Add(cmd.ExecuteScalar() as string ?? "");
        return rows;
    }

    [Fact]
    public void MergeUnionsRecordsByKeyAndKeepsLocalOnlyRows()
    {
        using var fixture = new StoreFixture();
        var account = Account(puuid: "self"); fixture.Store.Save(account);
        var local = new AccountCache { MatchesUpdatedAt = At };
        local.Ranks.Add(Snapshot(At)); local.Matches.Add(Match("JP1_1", "self")); local.Matches.Add(Match("OPGG_9", "self"));
        local.Opponents.Add(Opponent("enemy", At)); local.Forecasts.Add(Forecast(At)); local.QueueUpdatedAt[Queues.Flex] = At;
        fixture.Store.SaveCache(account.Id, local);
        var server = new AccountCache { MatchesUpdatedAt = At.AddHours(6) };
        server.Ranks.Add(Snapshot(At)); server.Ranks.Add(Snapshot(At.AddHours(6), 55));
        server.Matches.Add(Match("JP1_1", "self")); server.Matches.Add(Match("JP1_2", "self"));
        server.Opponents.Add(Opponent("enemy", At)); server.Opponents.Add(Opponent("enemy", At.AddHours(6)));
        server.Forecasts.Add(Forecast(At)); server.Forecasts.Add(Forecast(At.AddHours(6)));
        server.QueueUpdatedAt[Queues.Solo] = At.AddHours(6);

        var result = fixture.Store.MergeLolData(account.Id, "self", server);

        Assert.Equal(new LolMergeResult(LolMergeOutcome.Merged, Ranks: 1, Matches: 1, Opponents: 1, Forecasts: 1), result);
        Assert.Equal("試合+1 ランク+1 相手+1 予測+1", result.Summary);
        var cache = fixture.Store.Cache(account.Id);
        Assert.Equal(new[] { At, At.AddHours(6) }, cache.Ranks.Select(r => r.ObservedAt));
        Assert.Equal(new[] { "JP1_1", "JP1_2", "OPGG_9" }, cache.Matches.Select(m => m.Id).Order());
        Assert.Equal(2, cache.Opponents.Count); Assert.Equal(2, cache.Forecasts.Count);
        Assert.Equal(At.AddHours(6), cache.MatchesUpdatedAt);
        Assert.Equal([(Queues.Flex, At), (Queues.Solo, At.AddHours(6))], cache.QueueUpdatedAt.OrderBy(q => q.Key, StringComparer.Ordinal).Select(q => (q.Key, q.Value)));
        Assert.Equal("self", fixture.Store.Accounts().Single().Lol.Puuid);
    }

    [Fact]
    public void MergePrefersTheLocalMatchUnlessTheServerCopyHasLevelsOrATierItLacks()
    {
        using var fixture = new StoreFixture();
        var account = Account(puuid: "self"); fixture.Store.Save(account);
        var t1 = new MatchAverageTier(12, MatchAverageTier.Riot, At); var t2 = new MatchAverageTier(14, MatchAverageTier.Riot, At.AddHours(6));
        MatchRecord Levelled(string id, MatchAverageTier? tier) => Match(id, "self") with { LevelsRecorded = true, AverageTier = tier, Participants = [Player("self", true, 300)] };
        var local = new AccountCache();
        local.Matches.Add(Match("JP1_1", "self"));
        local.Matches.Add(Levelled("JP1_2", t1));
        local.Matches.Add(Levelled("JP1_3", null));
        local.Matches.Add(Match("JP1_4", "self") with { Participants = [Player("self", true) with { Champion = "Zed" }] });
        fixture.Store.SaveCache(account.Id, local);
        var server = new AccountCache();
        server.Matches.Add(Levelled("JP1_1", t2));
        server.Matches.Add(Levelled("JP1_2", t2));
        server.Matches.Add(Levelled("JP1_3", t2));
        server.Matches.Add(Match("JP1_4", "self"));

        var result = fixture.Store.MergeLolData(account.Id, "self", server);

        Assert.Equal(new LolMergeResult(LolMergeOutcome.Merged, UpgradedMatches: 2), result);
        var matches = fixture.Store.Cache(account.Id).Matches.ToDictionary(m => m.Id);
        Assert.True(matches["JP1_1"].LevelsRecorded); Assert.Equal(300, matches["JP1_1"].Participants.Single().SummonerLevel); Assert.Equal(t2, matches["JP1_1"].AverageTier);
        Assert.Equal(t1, matches["JP1_2"].AverageTier);
        Assert.Equal(t2, matches["JP1_3"].AverageTier);
        Assert.Equal("Zed", matches["JP1_4"].Participants.Single().Champion); Assert.Null(matches["JP1_4"].AverageTier);
    }

    [Fact]
    public void MergeKeepsTheLaterTimestampOnEachSide()
    {
        using var fixture = new StoreFixture();
        var account = Account(puuid: "self"); fixture.Store.Save(account);
        var local = new AccountCache { MatchesUpdatedAt = At.AddHours(6) }; local.QueueUpdatedAt[Queues.Solo] = At; local.QueueUpdatedAt[Queues.Flex] = At.AddHours(6);
        fixture.Store.SaveCache(account.Id, local);
        var server = new AccountCache { MatchesUpdatedAt = At }; server.QueueUpdatedAt[Queues.Solo] = At.AddHours(6); server.QueueUpdatedAt[Queues.Flex] = At; server.QueueUpdatedAt[Queues.Normal] = At;

        var result = fixture.Store.MergeLolData(account.Id, "self", server);

        Assert.Equal(new LolMergeResult(LolMergeOutcome.Merged), result);
        var cache = fixture.Store.Cache(account.Id);
        Assert.Equal(At.AddHours(6), cache.MatchesUpdatedAt);
        Assert.Equal([(Queues.Normal, At), (Queues.Flex, At.AddHours(6)), (Queues.Solo, At.AddHours(6))],
            cache.QueueUpdatedAt.OrderBy(q => q.Key, StringComparer.Ordinal).Select(q => (q.Key, q.Value)));
    }

    [Fact]
    public void MergeFillsANullPuuidAndSkipsWhenPuuidsDiffer()
    {
        using var fixture = new StoreFixture();
        var account = new RiotAccount(Guid.NewGuid(), "Main", [new("lol", "Name", "JP1", "JP1"), new("tft", "Name", "JP1", "JP1", "tft-id")]);
        fixture.Store.Save(account, new("user", "pass"));
        var server = new AccountCache(); server.Matches.Add(Match("JP1_1", "remote"));

        var filled = fixture.Store.MergeLolData(account.Id, "remote", server);

        Assert.Equal(new LolMergeResult(LolMergeOutcome.Merged, Matches: 1, PuuidFilled: true), filled);
        Assert.Equal("試合+1 PUUID補完", filled.Summary);
        var saved = fixture.Store.Accounts().Single();
        Assert.Equal("remote", saved.Lol.Puuid);
        Assert.Equal("tft-id", saved.Profiles.Single(p => p.Game == "tft").Puuid);
        Assert.Equal(new Credentials("user", "pass"), fixture.Store.Credentials(account.Id));

        var before = Rows(fixture, account.Id);
        var other = new AccountCache(); other.Matches.Add(Match("JP1_2", "other")); other.Ranks.Add(Snapshot(At));
        Assert.Equal(new LolMergeResult(LolMergeOutcome.PuuidMismatch), fixture.Store.MergeLolData(account.Id, "other", other));
        Assert.Equal(before, Rows(fixture, account.Id));
        // A copy without a PUUID merges against whatever is saved.
        Assert.Equal(LolMergeOutcome.Merged, fixture.Store.MergeLolData(account.Id, null, other).Outcome);
        Assert.Equal("remote", fixture.Store.Accounts().Single().Lol.Puuid);
    }

    [Fact]
    public void MergeIsIdempotentAndWritesNothingWhenUnchanged()
    {
        using var fixture = new StoreFixture();
        var account = Account(puuid: "self"); fixture.Store.Save(account);
        var server = Remote(account, "self").Cache; server.Opponents.Add(Opponent("enemy", At)); server.Forecasts.Add(Forecast(At)); server.QueueUpdatedAt[Queues.Solo] = At;

        var first = fixture.Store.MergeLolData(account.Id, "self", server);
        var rows = Rows(fixture, account.Id);
        var second = fixture.Store.MergeLolData(account.Id, "self", server);

        Assert.Equal(new LolMergeResult(LolMergeOutcome.Merged, Ranks: 1, Matches: 1, Opponents: 1, Forecasts: 1), first);
        Assert.Equal(new LolMergeResult(LolMergeOutcome.Unchanged), second);
        Assert.Equal("変更なし", second.Summary);
        Assert.Equal(rows, Rows(fixture, account.Id));
        Assert.Throws<InvalidOperationException>(() => fixture.Store.MergeLolData(Guid.NewGuid(), "x", new()));
    }

    [Fact]
    public void MergeAdoptsTheServerPuuidOnRequestAndDropsRecordsKeyedByTheOldOne()
    {
        using var fixture = new StoreFixture();
        var account = new RiotAccount(Guid.NewGuid(), "Main", [new("lol", "Name", "JP1", "JP1", "old"), new("tft", "Name", "JP1", "JP1")]);
        fixture.Store.Save(account, new("user", "pass"));
        var old = new AccountCache { MatchesUpdatedAt = At }; old.Matches.Add(Match("JP1_1", "old")); old.Opponents.Add(Opponent("enemy", At));
        old.Ranks.Add(Snapshot(At.AddHours(-6))); old.Forecasts.Add(Forecast(At.AddHours(-6))); old.QueueUpdatedAt[Queues.Solo] = At;
        fixture.Store.SaveCache(account.Id, old);

        var result = fixture.Store.MergeLolData(account.Id, "remote", Remote(account).Cache, adoptServerPuuid: true);

        Assert.Equal(new LolMergeResult(LolMergeOutcome.Merged, Ranks: 1, Matches: 1, PuuidReplaced: true), result);
        var saved = fixture.Store.Accounts().Single();
        Assert.Equal("remote", saved.Lol.Puuid);
        Assert.Contains(saved.Profiles, p => p.Game == "tft");
        Assert.Equal(new Credentials("user", "pass"), fixture.Store.Credentials(account.Id));
        var cache = fixture.Store.Cache(account.Id);
        Assert.Equal("JP1_2", Assert.Single(cache.Matches).Id);
        Assert.Empty(cache.Opponents);
        Assert.Equal(2, cache.Ranks.Count); Assert.Single(cache.Forecasts);
        Assert.Equal(At, cache.MatchesUpdatedAt); Assert.Empty(cache.QueueUpdatedAt);
    }

    [Fact]
    public async Task ImportMergesRegistersAndSkipsPerAccountWithoutCallingRiot()
    {
        using var fixture = new StoreFixture(); using var server = new WebTestServer();
        var same = Account("Same", puuid: "remote"); var renamed = Account("Renamed", puuid: "remote");
        var missing = Account("Missing", puuid: null) with { Note = "メモ" }; var foreign = Account("Foreign", puuid: "other-key");
        fixture.Store.Save(same); fixture.Store.Save(renamed); fixture.Store.Save(missing, new("secret-user", "secret-pass")); fixture.Store.Save(foreign);
        var status = Status(changedAt: At);
        server.Route = request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/status") => Reply(HttpStatusCode.OK, status),
            ("GET", "/api/snapshot") => Reply(HttpStatusCode.OK, new WebSnapshot(At, [Remote(same), Remote(renamed, gameName: "Changed"), Remote(foreign)])),
            ("PUT", var path) when path == $"/api/accounts/{missing.Id}" => Reply(HttpStatusCode.OK, Remote(missing)),
            _ => new(HttpStatusCode.NotFound)
        };
        var progress = new Reports();

        var result = await Provider(fixture, server).ImportAllAsync(null, force: false, progress, default);

        Assert.Equal(WebImportState.Imported, result.State);
        Assert.Equal(status.ChangeMarker, result.Marker);
        Assert.Equal([("GET", "/api/status"), ("GET", "/api/snapshot"), ("PUT", $"/api/accounts/{missing.Id}")], server.Calls.Select(c => (c.Method, c.Path)));
        Assert.Equal(new WebAccountInput("Missing", "Name", "JP1", "JP1", "メモ"), JsonSerializer.Deserialize<WebAccountInput>(server.Calls[2].Body!, Json));
        Assert.DoesNotContain("secret", server.Calls[2].Body!);
        var states = result.Accounts.ToDictionary(a => a.Label, a => a.State);
        Assert.Equal(WebAccountImportState.Merged, states["Same"]);
        Assert.Equal(WebAccountImportState.IdentityMismatch, states["Renamed"]);
        Assert.Equal(WebAccountImportState.Registered, states["Missing"]);
        Assert.Equal(WebAccountImportState.PuuidMismatch, states["Foreign"]);
        Assert.Equal("JP1_2", Assert.Single(fixture.Store.Cache(same.Id).Matches).Id);
        Assert.Empty(fixture.Store.Cache(renamed.Id).Matches); Assert.Empty(fixture.Store.Cache(foreign.Id).Matches);
        Assert.Equal("other-key", fixture.Store.Accounts().Single(a => a.Id == foreign.Id).Lol.Puuid);
        Assert.Equal("Web版から取り込み：試合+1 ランク+1（1件）／登録 1件／未取込 2件（Riot ID相違・PUUID相違）", result.Message);
        Assert.Contains("別のAPIキー", result.Accounts.Single(a => a.Label == "Foreign").Reason);
        Assert.Contains("Foreign: PUUIDがWeb版と異なります", result.Detail); Assert.Contains("Same: 取り込み 試合+1 ランク+1", result.Detail);
        Assert.Contains("Web版から取り込み中…", progress.Values);
    }

    [Fact]
    public async Task ImportSkipsTheSnapshotWhenTheMarkerIsUnchangedUnlessForced()
    {
        using var fixture = new StoreFixture(); using var server = new WebTestServer();
        var account = Account(puuid: "remote"); fixture.Store.Save(account);
        var status = Status(changedAt: At);
        server.Route = request => request.RequestUri!.AbsolutePath == "/api/status" ? Reply(HttpStatusCode.OK, status) : Reply(HttpStatusCode.OK, new WebSnapshot(At, [Remote(account)]));
        var provider = Provider(fixture, server);

        var unchanged = await provider.ImportAllAsync(status.ChangeMarker, force: false, null, default);
        var forced = await provider.ImportAllAsync(status.ChangeMarker, force: true, null, default);
        var moved = await provider.ImportAllAsync("0:0", force: false, null, default);

        Assert.Equal(WebImportState.Unchanged, unchanged.State); Assert.Equal("Web版に新しい取得結果はありません。", unchanged.Message);
        Assert.Equal(WebImportState.Imported, forced.State); Assert.Equal("Web版から取り込み：試合+1 ランク+1（1件）", forced.Message);
        Assert.Equal(WebImportState.Imported, moved.State); Assert.Equal("Web版から取り込み：変更なし", moved.Message);
        Assert.Equal(["/api/status", "/api/status", "/api/snapshot", "/api/status", "/api/snapshot"], server.Calls.Select(c => c.Path));
    }

    [Fact]
    public async Task ImportWaitsWhileAServerJobRunsUnlessForced()
    {
        using var fixture = new StoreFixture(); using var server = new WebTestServer();
        var account = Account(puuid: "remote"); fixture.Store.Save(account);
        var status = Status(current: Job(Guid.NewGuid(), WebJob.Running, "取得中"), changedAt: At);
        server.Route = request => request.RequestUri!.AbsolutePath == "/api/status" ? Reply(HttpStatusCode.OK, status) : Reply(HttpStatusCode.OK, new WebSnapshot(At, [Remote(account)]));

        var busy = await Provider(fixture, server).ImportAllAsync(null, force: false, null, default);
        Assert.Equal(["/api/status"], server.Calls.Select(c => c.Path));
        var forced = await Provider(fixture, server).ImportAllAsync(null, force: true, null, default);

        Assert.Equal(WebImportState.ServerBusy, busy.State); Assert.Empty(busy.Accounts); Assert.StartsWith("Web版が更新中", busy.Message);
        Assert.Equal(WebImportState.Imported, forced.State);
        Assert.Equal(3, server.Calls.Count);
    }

    [Fact]
    public void ChangeMarkerFallsBackToTheLastFullRunForOlderServers()
    {
        var run = new RefreshResult(At, At.AddMinutes(5), RefreshStop.None, []);
        Assert.Equal("0:0", Status().ChangeMarker);
        Assert.Equal($"0:{At.AddMinutes(5).UtcTicks}", Status(lastRun: run).ChangeMarker);
        Assert.Equal($"{At.UtcTicks}:{At.AddMinutes(5).UtcTicks}", Status(lastRun: run, changedAt: At).ChangeMarker);
        // An older server's JSON has no lastChangedAt; a newer client reads it as null and the marker is not serialized.
        var legacy = JsonSerializer.Deserialize<WebStatus>("""{"version":"1.3.0","keyRegistered":true,"current":null,"queued":0,"lastRun":null,"nextDue":"2026-10-12T09:00:00+00:00","intervalHours":6,"rateLimits":[]}""", Json)!;
        Assert.Null(legacy.LastChangedAt); Assert.Equal("0:0", legacy.ChangeMarker);
        var json = JsonSerializer.Serialize(Status(changedAt: At), Json);
        Assert.DoesNotContain("changeMarker", json); Assert.Contains("\"lastChangedAt\":\"2026-10-12T03:00:00+00:00\"", json);
    }

    [Fact]
    public async Task ImportedMatchesAndOpponentsSurviveALocalRiotRefresh()
    {
        using var fixture = new StoreFixture();
        var account = Account(puuid: "self"); fixture.Store.Save(account);
        var recent = DateTimeOffset.UtcNow.AddHours(-2);
        var server = new AccountCache { MatchesUpdatedAt = At };
        server.Matches.Add(Match("JP1_9", "self", recent) with { LevelsRecorded = true, Participants = [Player("self", true, 300), Player("enemy", false, 200)] });
        foreach (var queue in Queues.Ranked) server.Opponents.Add(Opponent("enemy", DateTimeOffset.UtcNow.AddMinutes(-10), queue));
        Assert.Equal(LolMergeOutcome.Merged, fixture.Store.MergeLolData(account.Id, "self", server).Outcome);
        var paths = new List<string>();
        using var handler = new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath; paths.Add(path);
            if (path.EndsWith("/by-puuid/self")) return Task.FromResult(Reply(HttpStatusCode.OK, new[] { new RankEntry(Queues.Solo, "GOLD", "II", 40, 10, 8) }));
            if (path.EndsWith("/ids")) return Task.FromResult(Reply(HttpStatusCode.OK, new[] { "JP1_0" }));
            if (path.EndsWith("/matches/JP1_0")) return Task.FromResult(Reply(HttpStatusCode.OK, ApiMatch("JP1_0", "self", recent.AddHours(1))));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");

        await new LolStatsProvider(fixture.Store, api).RefreshAnalysisAsync(account, Queues.Solo, 20, new Reports(), default);

        var cache = fixture.Store.Cache(account.Id);
        Assert.Equal(["JP1_0", "JP1_9"], cache.Matches.Select(m => m.Id).Order());
        Assert.DoesNotContain(paths, p => p.EndsWith("/by-puuid/enemy"));
        Assert.Equal(2, cache.Opponents.Count(o => o.Puuid == "enemy"));
        Assert.Single(cache.Ranks);
    }

    [Fact]
    public async Task AFilledPuuidSparesTheNextRiotRefreshTheAccountLookup()
    {
        using var fixture = new StoreFixture();
        var account = Account(puuid: null); fixture.Store.Save(account);
        Assert.True(fixture.Store.MergeLolData(account.Id, "remote", new()).PuuidFilled);
        var paths = new List<string>();
        using var handler = new Handler((request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Reply(HttpStatusCode.OK, new[] { new RankEntry(Queues.Solo, "GOLD", "II", 40, 10, 8) }));
        });
        using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "test");

        await new LolStatsProvider(fixture.Store, api).RefreshRanksAsync(fixture.Store.Accounts().Single(), default);

        Assert.EndsWith("/by-puuid/remote", Assert.Single(paths));
        Assert.Single(fixture.Store.Cache(account.Id).Ranks);
    }

    [Fact]
    public async Task ImportLogsCountsOnlyAndNamesNothing()
    {
        using var fixture = new StoreFixture(); using var server = new WebTestServer();
        var account = Account("秘密のラベル", puuid: "remote"); fixture.Store.Save(account);
        server.Route = request => request.RequestUri!.AbsolutePath == "/api/status" ? Reply(HttpStatusCode.OK, Status(changedAt: At)) : Reply(HttpStatusCode.OK, new WebSnapshot(At, [Remote(account)]));
        var log = new DiagnosticLog(Path.Combine(fixture.Directory, "diagnostics.log"));

        await Provider(fixture, server, log).ImportAllAsync(null, force: true, null, default);
        server.Route = _ => throw new HttpRequestException("down");
        await Assert.ThrowsAsync<WebSourceException>(() => Provider(fixture, server, log).ImportAllAsync(null, force: true, null, default));

        var text = File.ReadAllText(log.FilePath);
        Assert.Contains("[取り込み] Web版 開始", text);
        Assert.Contains("試合+1 補完0 ランク+1 相手+0 予測+0 登録0 未取込0", text);
        Assert.Contains("[取り込み] Web版 失敗 WebSourceException", text);
        Assert.DoesNotContain("remote", text); Assert.DoesNotContain("秘密のラベル", text); Assert.DoesNotContain(account.Id.ToString(), text); Assert.DoesNotContain("Name", text);
    }
}
