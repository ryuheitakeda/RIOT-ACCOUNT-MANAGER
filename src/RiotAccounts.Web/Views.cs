using System.Globalization;
using System.Text.Json;
using RiotAccounts.Core;

namespace RiotAccounts.Web;

public static class Format
{
    // The server runs on the user's Mac, so local time is the user's time zone.
    public static string Time(DateTimeOffset? at) =>
        at is { } t && t != DateTimeOffset.MinValue ? t.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture) : "—";
    public static string Short(DateTimeOffset at) => at.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);
    public static string QueueName(string? queue) => queue == null ? "ランク" : Queues.Get(queue).Name;
    public static DateTimeOffset? LastFetched(AccountCache cache) => new[] { cache.Ranks.LastOrDefault()?.ObservedAt, cache.MatchesUpdatedAt }.Max();
    public static string Duration(int seconds) => $"{seconds / 60}:{seconds % 60:00}";
    // "未取得" before the first observation, "UNRANKED" when Riot reported no entry for the queue.
    public static string Rank(AccountCache cache, string queue) =>
        cache.Ranks.LastOrDefault() is not { } latest ? "未取得" : latest.Entries.FirstOrDefault(e => e.QueueType == queue)?.Display ?? "UNRANKED";

    public static string Run(RefreshResult? run) => run switch
    {
        null => "まだありません",
        { Stop: RefreshStop.Key } => $"{Time(run.CompletedAt)}（APIキー拒否で中断）",
        { Stop: RefreshStop.RateLimit } => $"{Time(run.CompletedAt)}（取得制限で中断）",
        { Stop: RefreshStop.Network } => $"{Time(run.CompletedAt)}（接続できず中断）",
        _ when run.Failures > 0 => $"{Time(run.CompletedAt)}（{run.Failures}件失敗）",
        _ => $"{Time(run.CompletedAt)}（成功）"
    };

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

// What app.js draws: one line per segment (x = Unix ms). Rank history breaks the line where the tier or division changes.
public sealed record ChartPoint(long X, double Y, string Label);
public sealed record ChartSegment(string Label, List<ChartPoint> Points);
public sealed record ChartSpec(string Kind, List<ChartSegment> Segments, int? YMin = null, int? YMax = null, Dictionary<int, string>? YLabels = null);

public static class History
{
    public sealed record RankRow(string Period, string Rank, string Change, int Observations);
    public sealed record TierRow(string When, string Tier, bool Win, string Champion, string Source);

    // Ranked queues: the LP of every observation. Observations are not matches, so no per-match LP change is derived.
    public static (ChartSpec Chart, List<RankRow> Rows) Ranks(AccountCache cache, string queue)
    {
        var points = cache.Ranks.Select(s => (At: s.ObservedAt, Rank: s.Entries.FirstOrDefault(r => r.QueueType == queue))).ToList();
        var segments = new List<ChartSegment>();
        List<ChartPoint>? open = null;
        string? current = null;
        foreach (var point in points)
        {
            var label = point.Rank == null ? null : $"{point.Rank.Tier} {point.Rank.Rank}";
            if (label != current) { open = null; current = label; }
            if (point.Rank == null) continue;
            if (open == null) segments.Add(new(label!, open = []));
            open.Add(new(point.At.ToUnixTimeMilliseconds(), point.Rank.LeaguePoints, point.Rank.Display));
        }
        // Six-hourly refreshes repeat the same observation many times, so consecutive identical ones share a row.
        var rows = new List<RankRow>();
        for (var i = 0; i < points.Count;)
        {
            var display = points[i].Rank?.Display ?? "UNRANKED";
            var end = i;
            while (end + 1 < points.Count && (points[end + 1].Rank?.Display ?? "UNRANKED") == display) end++;
            var before = i > 0 ? points[i - 1].Rank?.Order : null;
            var after = points[i].Rank?.Order;
            var change = before.HasValue && after.HasValue && before != after ? after > before ? "↑ 昇格" : "↓ 降格" : "";
            var period = i == end ? Format.Time(points[i].At) : $"{Format.Time(points[i].At)} 〜 {Format.Time(points[end].At)}";
            rows.Add(new(period, display, change, end - i + 1));
            i = end + 1;
        }
        rows.Reverse();
        return (new ChartSpec("lp", segments), rows);
    }

    // Normal games: each match's lobby average tier, recorded once when the match was refreshed.
    public static (ChartSpec Chart, List<TierRow> Rows) AverageTiers(AccountCache cache, string puuid, string queue)
    {
        var matches = Analytics.Recent(cache, puuid, queue, int.MaxValue).Where(m => m.AverageTier != null).OrderBy(m => m.StartedAt).ToList();
        var rows = matches.Select(m =>
        {
            var tier = m.AverageTier!;
            var player = m.Participants.Single(p => p.Puuid == puuid);
            var source = tier.Source == MatchAverageTier.Opgg ? "OP.GG"
                : $"Riot API {tier.ObservedAt.ToLocalTime():MM/dd}時点・{tier.KnownPlayers}人{(tier.EstimatedPlayers > 0 ? $"（うちレベル推定{tier.EstimatedPlayers}人）" : "")}";
            return new TierRow(Format.Time(m.StartedAt), $"{tier.Label}（{tier.Order:F1}）", player.Win, player.Champion, source);
        }).ToList();
        if (matches.Count == 0) return (new ChartSpec("tier", []), rows);
        var values = matches.Select(m => m.AverageTier!.Order).ToList();
        var low = Math.Max(0, (int)Math.Floor(values.Min()) - 1);
        var high = Math.Min(30, (int)Math.Ceiling(values.Max()) + 1);
        var step = high - low > 12 ? 4 : 1;
        var labels = Enumerable.Range(low, high - low + 1).Where(o => o % step == 0 || o >= 28).ToDictionary(o => o, RankOrder.Label);
        var points = matches.Zip(rows, (m, row) => new ChartPoint(m.StartedAt.ToUnixTimeMilliseconds(), Math.Round(m.AverageTier!.Order, 2),
            $"{row.Tier} {(row.Win ? "勝利" : "敗北")} {row.Champion}")).ToList();
        rows.Reverse();
        return (new ChartSpec("tier", [new("平均ティア", points)], low, high, labels), rows);
    }
}
