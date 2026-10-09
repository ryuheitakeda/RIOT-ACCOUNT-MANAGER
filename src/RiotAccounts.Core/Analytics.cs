namespace RiotAccounts.Core;

public sealed record AccountOverview(string Rank, string LastPlayed);
public sealed record ScoreboardRow(string Champion, string Role, string Kda, string CsPerMinute, int VisionScore, bool IsSelf);
public sealed record ScoreboardTeam(string Title, List<ScoreboardRow> Players);

public static class Analytics
{
    // Built from saved observations only; the list must not trigger any fetch.
    public static AccountOverview Overview(AccountCache cache, DateTimeOffset now)
    {
        var latest = cache.Ranks.MaxBy(s => s.ObservedAt);
        var rank = latest == null ? "ランク未取得"
            : latest.Entries.FirstOrDefault(e => e.QueueType == Queues.Solo) is { } solo ? $"Solo {solo.Display}"
            : latest.Entries.FirstOrDefault(e => e.QueueType == Queues.Flex) is { } flex ? $"Flex {flex.Display}"
            : "UNRANKED";
        var last = cache.Matches.Count == 0 ? (DateTimeOffset?)null : cache.Matches.Max(m => m.StartedAt);
        return new(rank, last is { } at ? Ago(at, now) : "試合未取得");
    }

    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var days = (now.Date - at.ToOffset(now.Offset).Date).Days;
        return days <= 0 ? "今日" : days == 1 ? "昨日" : $"{days}日前";
    }

    private static readonly string[] RoleOrder = ["TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY"];

    public static string RoleName(string role) => role switch
    {
        "TOP" => "トップ", "JUNGLE" => "ジャングル", "MIDDLE" => "ミッド", "BOTTOM" => "ボット", "UTILITY" => "サポート", _ => "—"
    };

    // Other players are shown by champion and stats only; their names and PUUIDs are never displayed.
    public static List<ScoreboardTeam> Scoreboard(MatchRecord match, string puuid)
    {
        var minutes = Math.Max(1, match.DurationSeconds) / 60.0;
        return match.Participants.GroupBy(p => p.TeamId)
            .OrderByDescending(g => g.Any(p => p.Puuid == puuid)).ThenBy(g => g.Key)
            .Select(g => new ScoreboardTeam(
                $"{(g.Key == 200 ? "レッド" : "ブルー")}チーム — {(g.Any(p => p.Win) ? "勝利" : "敗北")}" + (g.Any(p => p.Puuid == puuid) ? "（自分）" : ""),
                g.OrderBy(p => Array.IndexOf(RoleOrder, p.Role) is var i and >= 0 ? i : RoleOrder.Length)
                    .Select(p => new ScoreboardRow(p.Champion, RoleName(p.Role), $"{p.Kills} / {p.Deaths} / {p.Assists}",
                        (p.Cs / minutes).ToString("F1"), p.VisionScore, p.Puuid == puuid)).ToList()))
            .ToList();
    }

    public static List<MatchRecord> Recent(AccountCache cache, string puuid, string queue, int count)
    {
        var definition = Queues.Get(queue);
        var eligible = cache.Matches
            .Where(m => definition.QueueIds.Contains(m.QueueId) && !m.Remake && m.DurationSeconds > 0 && m.Participants.Count(p => p.Puuid == puuid) == 1)
            .DistinctBy(m => m.Id).OrderByDescending(m => m.StartedAt).ToList();
        // The same game fetched from both sources has unrelated IDs; keep the Riot API copy.
        var riot = eligible.Where(m => !m.Id.StartsWith(OpggStatsProvider.MatchIdPrefix, StringComparison.Ordinal)).ToList();
        return eligible.Where(m => !m.Id.StartsWith(OpggStatsProvider.MatchIdPrefix, StringComparison.Ordinal) || !riot.Any(r => SameGame(r, m, puuid)))
            .Take(count).ToList();
    }

    private static bool SameGame(MatchRecord left, MatchRecord right, string puuid)
    {
        // OP.GG reports game creation and Riot the post-loading start, so allow a gap of a few minutes.
        if (left.QueueId != right.QueueId || (left.StartedAt - right.StartedAt).Duration() > TimeSpan.FromMinutes(10)) return false;
        var a = left.Participants.Single(p => p.Puuid == puuid);
        var b = right.Participants.Single(p => p.Puuid == puuid);
        return a.Champion == b.Champion && a.Kills == b.Kills && a.Deaths == b.Deaths && a.Assists == b.Assists && a.Win == b.Win;
    }

    public static List<Performance> Summarize(IEnumerable<MatchRecord> matches, string puuid, Func<Participant, string> group)
    {
        return Played(matches, puuid).GroupBy(x => group(x.Player))
            .Select(g => Aggregate(g.Key, g.ToList()))
            .OrderByDescending(x => x.Games).ThenBy(x => x.Name).ToList();
    }

    // Each account counts its own games, so a game shared by two own accounts (a duo) counts once per account.
    public static List<ChampionAcrossAccounts> ChampionsAcrossAccounts(IEnumerable<(string Label, AccountCache Cache, string Puuid)> accounts, string queue, int count)
    {
        return accounts.SelectMany(a => Played(Recent(a.Cache, a.Puuid, queue, count), a.Puuid).Select(x => (a.Label, x.Match, x.Player)))
            .GroupBy(x => x.Player.Champion)
            .Select(g => new ChampionAcrossAccounts(Aggregate(g.Key, g.Select(x => (x.Match, x.Player)).ToList()),
                g.GroupBy(x => x.Label).Select(a => (Account: a.Key, Games: a.Count()))
                    .OrderByDescending(a => a.Games).ThenBy(a => a.Account, StringComparer.CurrentCultureIgnoreCase).ToList()))
            .OrderByDescending(x => x.Total.Games).ThenBy(x => x.Total.Name).ToList();
    }

    private static IEnumerable<(MatchRecord Match, Participant Player)> Played(IEnumerable<MatchRecord> matches, string puuid) =>
        matches.DistinctBy(m => m.Id).Where(m => !m.Remake && m.DurationSeconds > 0)
            .Select(m => (Match: m, Player: m.Participants.SingleOrDefault(p => p.Puuid == puuid)))
            .Where(x => x.Player != null).Select(x => (x.Match, x.Player!));

    private static Performance Aggregate(string name, List<(MatchRecord Match, Participant Player)> games) => new(name, games.Count, games.Count(x => x.Player.Win),
        games.Sum(x => x.Player.Kills + x.Player.Assists) / (double)Math.Max(1, games.Sum(x => x.Player.Deaths)),
        games.Sum(x => x.Player.Cs) / (games.Sum(x => x.Match.DurationSeconds) / 60.0),
        games.Average(x => x.Player.VisionScore));

    // Forecast window: the minimum match count the forecast needs, so opponent rank lookups stay at most 10 x 5.
    public const int ForecastMatches = 10;
    // One league lookup returns every ranked queue, so an observation is reused by all queues' refreshes for this long.
    public static readonly TimeSpan OpponentRankTtl = TimeSpan.FromDays(3);

    // Normal games have no rank of their own, so an opponent's Solo/Duo rank (or Flex when unranked there) stands in for it.
    public static IReadOnlyList<string> ReferenceQueues(string queue) => Queues.IsRanked(queue) ? [queue] : Queues.Ranked;
    // In normal games an unranked opponent is estimated from the ranked opponents with the nearest summoner levels.
    public const int LevelNeighbours = 5;

    // The matches a forecast is based on: the latest ForecastMatches of the queue in the last 30 days.
    public static List<MatchRecord> ForecastWindow(AccountCache cache, string puuid, string queue, DateTimeOffset now) =>
        Recent(cache, puuid, queue, int.MaxValue).Where(m => m.StartedAt >= now.AddDays(-30) && m.StartedAt <= now).Take(ForecastMatches).ToList();

    // (summoner level, rank order) of every opponent with both a current rank observation and a recorded level in any of the caches.
    // Each player counts once: the Solo/Duo rank (Flex when unranked there) and the level from their most recent stored match.
    public static List<(int Level, int Order)> LevelCalibration(IEnumerable<AccountCache> caches, DateTimeOffset now)
    {
        var all = caches.ToList();
        var observations = all.SelectMany(c => c.Opponents)
            .Where(o => Queues.Ranked.Contains(o.QueueType) && o.ObservedAt <= now && o.ObservedAt >= now - OpponentRankTtl)
            .GroupBy(o => (o.Puuid, o.QueueType)).ToDictionary(g => g.Key, g => g.MaxBy(o => o.ObservedAt)!);
        var levels = all.SelectMany(c => c.Matches).SelectMany(m => m.Participants.Where(p => p.SummonerLevel > 0).Select(p => (p.Puuid, p.SummonerLevel, m.StartedAt)))
            .GroupBy(x => x.Puuid).ToDictionary(g => g.Key, g => g.MaxBy(x => x.StartedAt).SummonerLevel);
        var result = new List<(int Level, int Order)>();
        foreach (var (puuid, level) in levels)
            if (Queues.Ranked.Select(q => observations.GetValueOrDefault((puuid, q))).FirstOrDefault(o => o?.Rank is { } rank && rank.QueueType == o.QueueType && rank.Order != null) is { } found)
                result.Add((level, found.Rank!.Order!.Value));
        return result;
    }

    // calibrationSources: the caches whose opponents calibrate the level estimate (all accounts in the app); defaults to this cache.
    // The latest in-TTL observation per (player, queue) for the given ranked queues.
    private static Dictionary<(string Puuid, string QueueType), OpponentRankObservation> CurrentObservations(AccountCache cache, IReadOnlyList<string> queues, DateTimeOffset now) =>
        cache.Opponents.Where(o => queues.Contains(o.QueueType) && o.ObservedAt <= now && o.ObservedAt >= now - OpponentRankTtl)
            .GroupBy(o => (o.Puuid, o.QueueType)).ToDictionary(g => g.Key, g => g.MaxBy(o => o.ObservedAt)!);

    // The rank median of the LevelNeighbours calibration players with the nearest summoner levels; null when calibration is too small.
    private static int? EstimateFromLevel(IReadOnlyList<(int Level, int Order)> calibration, int level)
    {
        if (calibration.Count < LevelNeighbours) return null;
        var neighbours = calibration.OrderBy(k => Math.Abs(k.Level - level)).ThenBy(k => k.Level).Take(LevelNeighbours).Select(k => k.Order).Order().ToList();
        return neighbours[(neighbours.Count - 1) / 2];
    }

    // A match's average tier needs a rank (or level estimate) for at least this many of its 10 participants.
    public const int AverageTierMinPlayers = 8;

    // The mean of every participant's current Solo/Duo rank (Flex when unranked there), self included, as OP.GG averages the whole lobby.
    // Self comes from the account's latest in-TTL rank snapshot. Unranked participants known in both queues are estimated
    // from their summoner level; null when too few ranks are known.
    public static MatchAverageTier? AverageTier(MatchRecord match, string puuid, AccountCache cache, DateTimeOffset now, IReadOnlyList<(int Level, int Order)> calibration)
    {
        var observations = CurrentObservations(cache, Queues.Ranked, now);
        if (cache.Ranks.Where(s => s.ObservedAt <= now && s.ObservedAt >= now - OpponentRankTtl).MaxBy(s => s.ObservedAt) is { } own)
            foreach (var queue in Queues.Ranked)
                observations[(puuid, queue)] = new(puuid, queue, own.ObservedAt, own.Entries.FirstOrDefault(e => e.QueueType == queue));
        var values = new List<int>();
        var estimated = 0;
        foreach (var player in match.Participants.Where(p => !string.IsNullOrWhiteSpace(p.Puuid)).DistinctBy(p => p.Puuid))
        {
            var observed = Queues.Ranked.Select(q => observations.GetValueOrDefault((player.Puuid, q))).ToList();
            if (observed.FirstOrDefault(o => o?.Rank is { } rank && rank.QueueType == o.QueueType && rank.Order != null) is { } found)
                values.Add(found.Rank!.Order!.Value);
            else if (player.SummonerLevel > 0 && observed.All(o => o != null) && EstimateFromLevel(calibration, player.SummonerLevel) is { } guess)
            {
                values.Add(guess);
                estimated++;
            }
        }
        return values.Count < AverageTierMinPlayers ? null : new(values.Average(), MatchAverageTier.Riot, now, values.Count, estimated);
    }

    public static Forecast Predict(AccountCache cache, string puuid, string queue, DateTimeOffset now, IEnumerable<AccountCache>? calibrationSources = null)
    {
        var ranked = Queues.IsRanked(queue);
        var reference = ReferenceQueues(queue);
        var matches = ForecastWindow(cache, puuid, queue, now);
        var observations = CurrentObservations(cache, reference, now);
        var known = new List<(int Order, int Level, DateTimeOffset ObservedAt)>();
        var unranked = new List<(int Level, DateTimeOffset ObservedAt)>();
        var total = 0;
        foreach (var match in matches)
        {
            var team = match.Participants.Single(p => p.Puuid == puuid).TeamId;
            var enemies = match.Participants.Where(p => p.TeamId != team && !string.IsNullOrWhiteSpace(p.Puuid)).DistinctBy(p => p.Puuid).ToList();
            total += 5; // Missing participants must count as missing, not improve coverage.
            foreach (var enemy in enemies.Take(5))
            {
                var observed = reference.Select(q => observations.GetValueOrDefault((enemy.Puuid, q))).ToList();
                if (observed.FirstOrDefault(o => o?.Rank is { } rank && rank.QueueType == o.QueueType && rank.Order != null) is { } found)
                    known.Add((found.Rank!.Order!.Value, enemy.SummonerLevel, found.ObservedAt));
                // Only an opponent looked up in every reference queue is known to be unranked; a missing lookup stays missing.
                else if (!ranked && enemy.SummonerLevel > 0 && observed.All(o => o != null))
                    unranked.Add((enemy.SummonerLevel, observed.Max(o => o!.ObservedAt)));
            }
        }
        var values = known.Select(k => k.Order).ToList();
        var observedAt = known.Select(k => k.ObservedAt).ToList();
        var estimated = 0;
        var calibration = ranked || unranked.Count == 0 ? [] : LevelCalibration(calibrationSources ?? [cache], now);
        foreach (var (level, at) in unranked)
            if (EstimateFromLevel(calibration, level) is { } guess)
            {
                values.Add(guess);
                observedAt.Add(at);
                estimated++;
            }
        var basis = ranked ? "過去の対戦相手の現在ランク" : "過去の対戦相手のSolo/Duo（なければFlex）の現在ランク";
        if (matches.Count < 10 || total == 0 || values.Count * 1.0 / total < .8)
            return new(queue, now, matches.Count, values.Count, total, null, null, null,
                ranked ? "データ不足：直近30日で10戦以上・相手ランク取得率80%以上が必要です。" : "データ不足：直近30日で10戦以上・相手ランク取得率（レベル推定を含む）80%以上が必要です。")
            { EstimatedPlayers = estimated };
        values.Sort();
        string Quantile(double p) => RankOrder.Label(values[(int)Math.Ceiling(p * values.Count) - 1]);
        var method = ranked ? "" : $"未ランクの相手は、全アカウントの保存済み試合に出たランク持ちの相手のうちサモナーレベルが近い{LevelNeighbours}人のランク中央値で推定します。";
        return new(queue, now, matches.Count, values.Count, total, Quantile(.25), Quantile(.5), Quantile(.75),
            $"{basis}の中央50%です。{method}同じ相手も対戦ごとに1件とし、分位点は順位カテゴリのnearest-rank法で計算します。次戦の保証・内部MMRではありません。")
        {
            OldestRankObservedAt = observedAt.Min(),
            LatestRankObservedAt = observedAt.Max(),
            EstimatedPlayers = estimated
        };
    }
}
