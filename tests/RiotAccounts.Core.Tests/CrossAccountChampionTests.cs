using RiotAccounts.Core;
using Xunit;

public sealed class CrossAccountChampionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static MatchRecord Match(string id, int minutesAgo, params Participant[] players) =>
        new(id, 420, Now.AddMinutes(-minutesAgo), 1200, false, [.. players]);
    private static Participant P(string puuid, string champion, bool win, int kills = 3) => new(puuid, 100, champion, "MIDDLE", kills, 1, 1, 200, 10, win);

    [Fact]
    public void CombinesAccountsAndCountsSharedGamePerAccount()
    {
        var main = new AccountCache { Matches = [Match("m1", 10, P("a", "Ahri", true), P("b", "Lulu", true)), Match("m2", 20, P("a", "Ahri", false))] };
        var sub = new AccountCache
        {
            Matches =
            [
                Match("m1", 10, P("a", "Ahri", true), P("b", "Lulu", true)),
                Match("m3", 30, P("b", "Ahri", true, kills: 9)),
                new("m4", 420, Now.AddMinutes(-40), 180, true, [P("b", "Ahri", false)]),
                new("m5", 400, Now.AddMinutes(-50), 1200, false, [P("b", "Ahri", false)])
            ]
        };
        var result = Analytics.ChampionsAcrossAccounts([("Main", main, "a"), ("Sub", sub, "b")], Queues.Solo, 20);
        var ahri = Assert.Single(result, r => r.Total.Name == "Ahri");
        Assert.Equal(3, ahri.Total.Games); // remake (m4) and normal (m5) are excluded
        Assert.Equal(2, ahri.Total.Wins);
        Assert.Equal((3 + 1 + 3 + 1 + 9 + 1) / 3.0, ahri.Total.Kda);
        Assert.Equal("Main 2戦  /  Sub 1戦", ahri.Breakdown);
        var lulu = Assert.Single(result, r => r.Total.Name == "Lulu");
        Assert.Equal("Sub 1戦", lulu.Breakdown);
        Assert.Equal("Ahri", result[0].Total.Name);
    }

    [Fact]
    public void RespectsPerAccountCount()
    {
        var cache = new AccountCache { Matches = [.. Enumerable.Range(0, 5).Select(i => Match($"m{i}", i, P("a", "Ahri", true)))] };
        Assert.Equal(2, Analytics.ChampionsAcrossAccounts([("Main", cache, "a")], Queues.Solo, 2).Single().Total.Games);
    }
}
