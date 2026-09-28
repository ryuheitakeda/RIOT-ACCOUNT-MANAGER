using RiotAccounts.Core;
using Xunit;

public sealed class AccountOverviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 1, 0, 0, TimeSpan.FromHours(9));
    private static RankEntry Entry(string queue, string tier, int lp) => new(queue, tier, "II", lp, 10, 8);
    private static MatchRecord Match(string id, DateTimeOffset at, int queue = 420) => new(id, queue, at, 1800, false,
        [new("self", 100, "Ahri", "MIDDLE", 5, 2, 7, 200, 20, true)]);

    [Fact]
    public void EmptyCacheShowsNotFetched()
    {
        var overview = Analytics.Overview(new AccountCache(), Now);
        Assert.Equal("ランク未取得", overview.Rank);
        Assert.Equal("試合未取得", overview.LastPlayed);
    }

    [Fact]
    public void PrefersSoloFromLatestSnapshot()
    {
        var cache = new AccountCache
        {
            Ranks =
            [
                new(Now.AddDays(-1), [Entry(Queues.Solo, "SILVER", 10)]),
                new(Now, [Entry(Queues.Flex, "PLATINUM", 5), Entry(Queues.Solo, "GOLD", 45)])
            ]
        };
        Assert.Equal($"Solo {Entry(Queues.Solo, "GOLD", 45).Display}", Analytics.Overview(cache, Now).Rank);
    }

    [Fact]
    public void FallsBackToFlexThenUnranked()
    {
        var flex = new AccountCache { Ranks = [new(Now, [Entry(Queues.Flex, "PLATINUM", 5)])] };
        Assert.StartsWith("Flex PLATINUM II", Analytics.Overview(flex, Now).Rank);
        // An older Solo observation must not be shown once the latest snapshot has none.
        var unranked = new AccountCache { Ranks = [new(Now.AddDays(-3), [Entry(Queues.Solo, "GOLD", 1)]), new(Now, [])] };
        Assert.Equal("UNRANKED", Analytics.Overview(unranked, Now).Rank);
    }

    [Fact]
    public void LastPlayedUsesNewestMatchOfAnyQueueInLocalDays()
    {
        var cache = new AccountCache { Matches = [Match("a", Now.AddDays(-5)), Match("b", Now.AddDays(-3), 400)] };
        Assert.Equal("3日前", Analytics.Overview(cache, Now).LastPlayed);
        // 23:30 the previous evening in UTC+9 is "yesterday", even though it is less than 24 hours ago.
        Assert.Equal("昨日", Analytics.Ago(new DateTimeOffset(2026, 9, 27, 14, 30, 0, TimeSpan.Zero), Now));
        Assert.Equal("今日", Analytics.Ago(Now.AddMinutes(-30), Now));
        Assert.Equal("今日", Analytics.Ago(Now.AddMinutes(5), Now));
    }
}
