using System.Text.Json.Serialization;

namespace RiotAccounts.Core;

public sealed record GameProfile(string Game, string GameName, string TagLine, string Platform, string? Puuid = null);
// Note is stored in plain text like the label; it must never hold credentials.
public sealed record RiotAccount(Guid Id, string Label, List<GameProfile> Profiles, string? Note = null)
{
    [JsonIgnore] public GameProfile Lol => Profiles.Single(p => p.Game == "lol");
    [JsonIgnore] public string RiotId => $"{Lol.GameName}#{Lol.TagLine}";
}
public sealed record Credentials(string Username, string Password);
public sealed record RankEntry(string QueueType, string Tier, string Rank, int LeaguePoints, int Wins, int Losses)
{
    [JsonIgnore] public int? Order => RankOrder.Parse(Tier, Rank);
    [JsonIgnore] public string Display => $"{Tier} {Rank}  {LeaguePoints} LP";
    [JsonIgnore] public string Record => $"{Wins}勝 {Losses}敗  /  {(Wins + Losses == 0 ? 0 : 100.0 * Wins / (Wins + Losses)):F1}%";
}
public sealed record RankSnapshot(DateTimeOffset ObservedAt, List<RankEntry> Entries);
// SummonerLevel is 0 when the source did not provide it (OP.GG, caches saved before it was recorded).
public sealed record Participant(string Puuid, int TeamId, string Champion, string Role, int Kills, int Deaths, int Assists, int Cs, int VisionScore, bool Win, int SummonerLevel = 0);
public sealed record MatchRecord(string Id, int QueueId, DateTimeOffset StartedAt, int DurationSeconds, bool Remake, List<Participant> Participants)
{
    // False for Riot API details saved before summoner levels were recorded; such matches are fetched again once.
    public bool LevelsRecorded { get; init; }
}
public sealed record OpponentRankObservation(string Puuid, string QueueType, DateTimeOffset ObservedAt, RankEntry? Rank);
public sealed record Forecast(string QueueType, DateTimeOffset CreatedAt, int MatchCount, int KnownPlayers, int TotalPlayers, string? Lower, string? Median, string? Upper, string Explanation)
{
    [JsonIgnore] public double MissingRate => TotalPlayers == 0 ? 1 : 1 - KnownPlayers / (double)TotalPlayers;
    public DateTimeOffset? OldestRankObservedAt { get; init; }
    public DateTimeOffset? LatestRankObservedAt { get; init; }
    // How many of KnownPlayers were unranked opponents whose rank was estimated from their summoner level (normal games only).
    public int EstimatedPlayers { get; init; }
}
public sealed class AccountCache
{
    public List<RankSnapshot> Ranks { get; set; } = [];
    public List<MatchRecord> Matches { get; set; } = [];
    public List<OpponentRankObservation> Opponents { get; set; } = [];
    public List<Forecast> Forecasts { get; set; } = [];
    public DateTimeOffset? MatchesUpdatedAt { get; set; }
    public Dictionary<string, DateTimeOffset> QueueUpdatedAt { get; set; } = [];
}
public sealed record Performance(string Name, int Games, int Wins, double Kda, double CsPerMinute, double VisionPerGame)
{
    public double WinRate => Games == 0 ? 0 : Wins * 100.0 / Games;
    public string Display => $"{Name}    {Games}戦  {WinRate:F0}%    KDA {Kda:F2}    CS/分 {CsPerMinute:F1}";
}
public sealed record ChampionAcrossAccounts(Performance Total, List<(string Account, int Games)> Accounts)
{
    public string Breakdown => string.Join("  /  ", Accounts.Select(a => $"{a.Account} {a.Games}戦"));
}
public sealed record QueueDefinition(string Key, string Name, IReadOnlyList<int> QueueIds, bool IsRanked);
public static class Queues
{
    public const string Solo = "RANKED_SOLO_5x5";
    public const string Flex = "RANKED_FLEX_SR";
    public const string Normal = "NORMAL";
    public static readonly IReadOnlyList<QueueDefinition> Definitions = Array.AsReadOnly(new[]
    {
        new QueueDefinition(Solo, "Solo / Duo", Array.AsReadOnly(new[] { 420 }), true),
        new QueueDefinition(Flex, "Flex", Array.AsReadOnly(new[] { 440 }), true),
        new QueueDefinition(Normal, "ノーマル", Array.AsReadOnly(new[] { 400, 430, 480, 490 }), false)
    });
    public static readonly IReadOnlyList<string> Ranked = Definitions.Where(q => q.IsRanked).Select(q => q.Key).ToList().AsReadOnly();
    public static QueueDefinition Get(string queue) => Definitions.SingleOrDefault(q => q.Key == queue)
        ?? throw new ArgumentException("非対応のキューです。", nameof(queue));
    public static bool Includes(string queue, int queueId) => Get(queue).QueueIds.Contains(queueId);
    public static bool IsRanked(string queue) => Get(queue).IsRanked;
    public static string MatchName(int queueId) => queueId switch
    {
        400 => "ドラフト", 430 => "ブラインド", 480 => "スイフトプレイ", 490 => "クイックプレイ",
        420 => "Solo / Duo", 440 => "Flex", _ => $"キュー {queueId}"
    };
    public static int Id(string queue) => queue switch { Solo => 420, Flex => 440, _ => throw new ArgumentException("非対応のキューです。") };
}
public static class Regions
{
    public static readonly IReadOnlyDictionary<string, string> Routing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["JP1"]="asia", ["KR"]="asia", ["NA1"]="americas", ["BR1"]="americas", ["LA1"]="americas", ["LA2"]="americas",
        ["EUW1"]="europe", ["EUN1"]="europe", ["TR1"]="europe", ["RU"]="europe", ["ME1"]="europe",
        ["OC1"]="sea", ["SG2"]="sea", ["TW2"]="sea", ["VN2"]="sea"
    };
    public static string Regional(string platform) => Routing.TryGetValue(platform, out var value) ? value : throw new ArgumentException("サーバーを選択してください。");
    // Account-v1 operates in three clusters; SEA platforms use the Asia account cluster.
    public static string AccountRegional(string platform) => Regional(platform) is "sea" ? "asia" : Regional(platform);
}
public static class RankOrder
{
    private static readonly string[] Tiers = ["IRON", "BRONZE", "SILVER", "GOLD", "PLATINUM", "EMERALD", "DIAMOND"];
    public static int? Parse(string tier, string division)
    {
        var index = Array.IndexOf(Tiers, tier);
        var d = division switch { "IV"=>0, "III"=>1, "II"=>2, "I"=>3, _=>-1 };
        if (index >= 0 && d >= 0) return index * 4 + d;
        return tier switch { "MASTER"=>28, "GRANDMASTER"=>29, "CHALLENGER"=>30, _=>null };
    }
    public static string Label(int order) => order switch
    {
        28 => "MASTER", 29 => "GRANDMASTER", 30 => "CHALLENGER",
        >=0 and <28 => $"{Tiers[order / 4]} {new[] { "IV", "III", "II", "I" }[order % 4]}",
        _ => "不明"
    };
}
