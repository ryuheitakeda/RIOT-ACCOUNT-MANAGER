using System.Net;
using System.Text.Json;
using RiotAccounts.Core;
using Xunit;

public sealed class RefreshTests
{
    static RiotAccount Account(string label) => new(Guid.NewGuid(), label, [new("lol", label, "JP1", "JP1", label + "-puuid")]);

    static HttpResponseMessage Ok(params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    [Fact]
    public async Task RateLimitHeadersAreRecordedPerRouteAndLogged()
    {
        var directory = Path.Combine(Path.GetTempPath(), "riot-refresh-tests-" + Guid.NewGuid());
        try
        {
            var log = new DiagnosticLog(Path.Combine(directory, "diagnostics.log"));
            using var handler = new Handler((_, _) => Task.FromResult(Ok(("X-App-Rate-Limit", "20:1,100:120"), ("X-App-Rate-Limit-Count", "1:1,3:120"),
                ("X-Method-Rate-Limit", "2000:10"), ("X-Method-Rate-Limit-Count", "1:10"))));
            using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "key", log);
            await api.GetAsync<List<RankEntry>>("jp1", "/lol/league/v4/entries/by-puuid/secret-puuid", default);
            await api.GetAsync<List<RankEntry>>("jp1", "/lol/league/v4/entries/by-puuid/other-puuid", default);

            var observed = Assert.Single(api.RateLimits);
            Assert.Equal(("jp1", "/lol/league/v4/entries/by-puuid/{id}"), (observed.Host, observed.Route));
            Assert.Equal(("20:1,100:120", "1:1,3:120", "2000:10", "1:10"), (observed.AppLimit, observed.AppCount, observed.MethodLimit, observed.MethodCount));
            Assert.Equal(200, observed.Status);
            var text = File.ReadAllText(log.FilePath);
            Assert.Contains("app=20:1,100:120 (1:1,3:120) method=2000:10 (1:10)", text);
            Assert.DoesNotContain("puuid", text.Replace("by-puuid", ""));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ResponsesWithoutRateLimitHeadersAreNotRecordedButServiceLimitsAre()
    {
        var n = 0;
        using var handler = new Handler((_, _) =>
        {
            if (++n == 2) return Task.FromResult(Ok());
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new(TimeSpan.FromSeconds(1));
            return Task.FromResult(limited);
        });
        using var client = new HttpClient(handler); using var api = new RiotApi(client, () => "key");
        await api.GetAsync<List<RankEntry>>("asia", "/lol/match/v5/matches/by-puuid/x/ids", default);

        var observed = Assert.Single(api.RateLimits);
        Assert.Equal(429, observed.Status);
        Assert.Null(observed.LimitType);
        Assert.Equal(1, observed.RetryAfterSeconds);
    }

    sealed class FakeProvider(Func<RiotAccount, string?, Task>? onCall = null) : IGameStatsProvider
    {
        public List<(Guid Account, string? Queue, int Count)> Calls { get; } = [];
        public Task RefreshRanksAsync(RiotAccount account, CancellationToken ct, IProgress<string>? progress = null)
        {
            Calls.Add((account.Id, null, 0)); progress?.Report("ranks");
            return onCall?.Invoke(account, null) ?? Task.CompletedTask;
        }
        public Task RefreshAnalysisAsync(RiotAccount account, string queue, int count, IProgress<string> progress, CancellationToken ct)
        {
            Calls.Add((account.Id, queue, count)); progress.Report("analysis");
            return onCall?.Invoke(account, queue) ?? Task.CompletedTask;
        }
    }

    sealed class Reports : IProgress<string> { public List<string> Values { get; } = []; public void Report(string value) => Values.Add(value); }

    [Fact]
    public async Task FullRunRefreshesEveryAccountAndQueueInDisplayOrder()
    {
        using var fixture = new StoreFixture();
        var first = Account("First"); var second = Account("Second");
        fixture.Store.Save(second); fixture.Store.Save(first);
        fixture.Store.SaveAccountOrder([second.Id, first.Id]);
        var provider = new FakeProvider(); var progress = new Reports();

        var result = await new RefreshCycle(fixture.Store, provider).RunAsync(new(), progress, default);

        Assert.Equal(new (Guid, string?, int)[]
            { (second.Id, Queues.Solo, 50), (second.Id, Queues.Flex, 50), (second.Id, Queues.Normal, 50), (first.Id, Queues.Solo, 50), (first.Id, Queues.Flex, 50), (first.Id, Queues.Normal, 50) },
            provider.Calls);
        Assert.Equal(RefreshStop.None, result.Stop);
        Assert.Equal(6, result.Outcomes.Count(o => o.Ok));
        Assert.Contains("Second（1/2）Solo / Duo: analysis", progress.Values);
        Assert.Contains("First（2/2）ノーマル: analysis", progress.Values);
    }

    [Fact]
    public async Task TargetsSelectOneAccountOneQueueOrRanksOnly()
    {
        using var fixture = new StoreFixture();
        var first = Account("First"); var second = Account("Second");
        fixture.Store.Save(first); fixture.Store.Save(second);
        var provider = new FakeProvider();
        var cycle = new RefreshCycle(fixture.Store, provider);

        await cycle.RunAsync(new(second.Id, Queues.Flex, 20), new Reports(), default);
        await cycle.RunAsync(new(first.Id, RanksOnly: true), new Reports(), default);

        Assert.Equal(new (Guid, string?, int)[] { (second.Id, Queues.Flex, 20), (first.Id, null, 0) }, provider.Calls);
        await Assert.ThrowsAsync<ArgumentException>(() => cycle.RunAsync(new(Guid.NewGuid()), new Reports(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => cycle.RunAsync(new(first.Id, "ARAM"), new Reports(), default));
    }

    [Fact]
    public async Task FailedStepIsRecordedAndTheRunContinues()
    {
        using var fixture = new StoreFixture();
        var first = Account("First"); var second = Account("Second");
        fixture.Store.Save(first); fixture.Store.Save(second);
        var unexpected = new List<string>();
        var provider = new FakeProvider((account, queue) =>
            account.Id == first.Id && queue == Queues.Flex ? throw new RiotApiException("対象データが見つかりません。", HttpStatusCode.NotFound)
            : account.Id == first.Id && queue == Queues.Normal ? throw new InvalidOperationException("bug")
            : Task.CompletedTask);

        var result = await new RefreshCycle(fixture.Store, provider, (_, queue, error) => unexpected.Add($"{queue}:{error.Message}")).RunAsync(new(), new Reports(), default);

        Assert.Equal(RefreshStop.None, result.Stop);
        Assert.Equal(6, provider.Calls.Count);
        Assert.Equal(2, result.Failures);
        Assert.Equal("対象データが見つかりません。", result.Outcomes.Single(o => o.Queue == Queues.Flex && o.AccountId == first.Id).Message);
        Assert.Equal("更新に失敗しました（InvalidOperationException）。", result.Outcomes.Single(o => o.Queue == Queues.Normal && o.AccountId == first.Id).Message);
        Assert.Equal(new[] { "NORMAL:bug" }, unexpected);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, RefreshStop.Key)]
    [InlineData(HttpStatusCode.Forbidden, RefreshStop.Key)]
    [InlineData(HttpStatusCode.TooManyRequests, RefreshStop.RateLimit)]
    public async Task KeyAndRateLimitFailuresStopTheRun(HttpStatusCode status, RefreshStop expected)
    {
        using var fixture = new StoreFixture();
        fixture.Store.Save(Account("First")); fixture.Store.Save(Account("Second"));
        var provider = new FakeProvider((_, _) => throw new RiotApiException("拒否", status));

        var result = await new RefreshCycle(fixture.Store, provider).RunAsync(new(), new Reports(), default);

        Assert.Equal(expected, result.Stop);
        Assert.Single(provider.Calls);
        Assert.False(Assert.Single(result.Outcomes).Ok);
    }

    [Fact]
    public async Task LostConnectionStopsTheRunAndCancellationPropagates()
    {
        using var fixture = new StoreFixture();
        fixture.Store.Save(Account("First"));
        var network = await new RefreshCycle(fixture.Store, new FakeProvider((_, _) => throw new HttpRequestException("down"))).RunAsync(new(), new Reports(), default);
        Assert.Equal(RefreshStop.Network, network.Stop);

        using var cancel = new CancellationTokenSource();
        var provider = new FakeProvider((_, _) => { cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RefreshCycle(fixture.Store, provider).RunAsync(new(), new Reports(), cancel.Token));
        Assert.Single(provider.Calls);
    }

    [Fact]
    public async Task AccountsDeletedOrRenamedDuringTheRunArePickedUp()
    {
        using var fixture = new StoreFixture();
        var first = Account("First"); var second = Account("Second"); var third = Account("Third");
        fixture.Store.Save(first); fixture.Store.Save(second); fixture.Store.Save(third);
        var provider = new FakeProvider((account, queue) =>
        {
            if (account.Id == first.Id && queue == Queues.Solo)
            {
                fixture.Store.Delete(second.Id);
                fixture.Store.Save(third with { Label = "Renamed" });
            }
            return Task.CompletedTask;
        });

        var result = await new RefreshCycle(fixture.Store, provider).RunAsync(new(), new Reports(), default);

        Assert.DoesNotContain(provider.Calls, c => c.Account == second.Id);
        Assert.Equal(6, provider.Calls.Count);
        Assert.All(result.Outcomes.Where(o => o.AccountId == third.Id), o => Assert.Equal("Renamed", o.Account));
    }

    [Fact]
    public void ScheduleWaitsAnIntervalAfterTheLastStartAndRetriesInterruptionsSooner()
    {
        var start = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        RefreshResult Run(RefreshStop stop) => new(start, start.AddMinutes(10), stop, []);
        var interval = RefreshSchedule.DefaultInterval;

        Assert.Equal(DateTimeOffset.MinValue, RefreshSchedule.NextDue(null, interval));
        Assert.Equal(start.AddHours(6), RefreshSchedule.NextDue(Run(RefreshStop.None), interval));
        Assert.Equal(start.AddHours(6), RefreshSchedule.NextDue(Run(RefreshStop.Key), interval));
        Assert.Equal(start.AddMinutes(40), RefreshSchedule.NextDue(Run(RefreshStop.RateLimit), interval));
        Assert.Equal(start.AddMinutes(40), RefreshSchedule.NextDue(Run(RefreshStop.Network), interval));
        Assert.Equal(start.AddMinutes(20), RefreshSchedule.NextDue(Run(RefreshStop.Network), TimeSpan.FromMinutes(20)));
    }

    [Fact]
    public void ResultAndContractRoundTripThroughStoreAndJson()
    {
        using var fixture = new StoreFixture();
        var at = new DateTimeOffset(2026, 10, 12, 1, 2, 3, TimeSpan.Zero);
        var result = new RefreshResult(at, at.AddMinutes(5), RefreshStop.RateLimit, [new(Guid.NewGuid(), "Main", Queues.Solo, false, "制限", at)]);
        fixture.Store.Write("web", "lastRun", result);
        var loaded = fixture.Store.Read<RefreshResult>("web", "lastRun")!;
        Assert.Equal((result.StartedAt, result.CompletedAt, result.Stop), (loaded.StartedAt, loaded.CompletedAt, loaded.Stop));
        Assert.Equal(result.Outcomes, loaded.Outcomes);

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var job = new WebJob(Guid.NewGuid(), WebJob.Running, "取得中", null, Guid.NewGuid(), Queues.Normal, false, at, at, null);
        Assert.Equal(job, JsonSerializer.Deserialize<WebJob>(JsonSerializer.Serialize(job, json), json));
        Assert.Contains("\"stop\":\"RateLimit\"", JsonSerializer.Serialize(result, json));
    }
}
