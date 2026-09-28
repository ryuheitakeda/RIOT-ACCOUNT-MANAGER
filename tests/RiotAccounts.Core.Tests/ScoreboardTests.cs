using RiotAccounts.Core;
using Xunit;

public sealed class ScoreboardTests
{
    private static Participant Player(string puuid, int team, string role, bool win) =>
        new(puuid, team, "Champ-" + puuid, role, 1, 2, 3, 150, 12, win);

    [Fact]
    public void OwnTeamComesFirstInRoleOrderWithSelfMarked()
    {
        var match = new MatchRecord("JP1_1", 420, DateTimeOffset.UnixEpoch, 1500, false,
        [
            Player("r1", 100, "UTILITY", true), Player("r2", 100, "TOP", true),
            Player("self", 200, "BOTTOM", false), Player("b2", 200, "", false), Player("b3", 200, "JUNGLE", false)
        ]);
        var teams = Analytics.Scoreboard(match, "self");
        Assert.Equal("レッドチーム — 敗北（自分）", teams[0].Title);
        Assert.Equal(["ジャングル", "ボット", "—"], teams[0].Players.Select(p => p.Role));
        Assert.True(teams[0].Players[1].IsSelf);
        Assert.Equal(1, teams.SelectMany(t => t.Players).Count(p => p.IsSelf));
        Assert.Equal("ブルーチーム — 勝利", teams[1].Title);
        Assert.Equal(["トップ", "サポート"], teams[1].Players.Select(p => p.Role));
        Assert.Equal("1 / 2 / 3", teams[1].Players[0].Kda);
        Assert.Equal((150 / 25.0).ToString("F1"), teams[1].Players[0].CsPerMinute);
    }
}
