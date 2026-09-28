namespace RiotAccounts.Core;

public sealed record AccountOverview(string Rank, string LastPlayed);

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
        return matches.DistinctBy(m => m.Id).Where(m => !m.Remake && m.DurationSeconds > 0)
            .Select(m => (Match: m, Player: m.Participants.SingleOrDefault(p => p.Puuid == puuid)))
            .Where(x => x.Player != null).GroupBy(x => group(x.Player!))
            .Select(g => new Performance(g.Key, g.Count(), g.Count(x => x.Player!.Win),
                g.Sum(x => x.Player!.Kills + x.Player.Assists) / (double)Math.Max(1, g.Sum(x => x.Player!.Deaths)),
                g.Sum(x => x.Player!.Cs) / (g.Sum(x => x.Match.DurationSeconds) / 60.0),
                g.Average(x => x.Player!.VisionScore)))
            .OrderByDescending(x => x.Games).ThenBy(x => x.Name).ToList();
    }

    public static Forecast Predict(AccountCache cache, string puuid, string queue, DateTimeOffset now)
    {
        if (!Queues.IsRanked(queue)) throw new ArgumentException("参考ランク帯はランク戦のみ対応しています。", nameof(queue));
        var matches = Recent(cache, puuid, queue, int.MaxValue)
            .Where(m => m.StartedAt >= now.AddDays(-30) && m.StartedAt <= now).Take(20).ToList();
        var observations = cache.Opponents.Where(o => o.QueueType == queue && o.ObservedAt <= now && o.ObservedAt >= now.AddHours(-24))
            .GroupBy(o => o.Puuid).ToDictionary(g => g.Key, g => g.MaxBy(o => o.ObservedAt)!);
        var values = new List<int>();
        var observedAt = new List<DateTimeOffset>();
        var total = 0;
        foreach (var match in matches)
        {
            var team = match.Participants.Single(p => p.Puuid == puuid).TeamId;
            var enemies = match.Participants.Where(p => p.TeamId != team && !string.IsNullOrWhiteSpace(p.Puuid)).DistinctBy(p => p.Puuid).ToList();
            total += 5; // Missing participants must count as missing, not improve coverage.
            foreach (var enemy in enemies.Take(5))
                if (observations.TryGetValue(enemy.Puuid, out var observed) && observed.Rank?.QueueType == queue && observed.Rank.Order is int order)
                {
                    values.Add(order);
                    observedAt.Add(observed.ObservedAt);
                }
        }
        if (matches.Count < 10 || total == 0 || values.Count * 1.0 / total < .8)
            return new(queue, now, matches.Count, values.Count, total, null, null, null, "データ不足：直近30日で10戦以上・相手ランク取得率80%以上が必要です。");
        values.Sort();
        string Quantile(double p) => RankOrder.Label(values[(int)Math.Ceiling(p * values.Count) - 1]);
        return new(queue, now, matches.Count, values.Count, total, Quantile(.25), Quantile(.5), Quantile(.75),
            "過去の対戦相手の現在ランクの中央50%です。同じ相手も対戦ごとに1件とし、分位点は順位カテゴリのnearest-rank法で計算します。次戦の保証・内部MMRではありません。")
        {
            OldestRankObservedAt = observedAt.Min(),
            LatestRankObservedAt = observedAt.Max()
        };
    }
}
