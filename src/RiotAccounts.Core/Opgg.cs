using System.Globalization;
using System.Net;
using System.Text.Json;

namespace RiotAccounts.Core;

// OP.GG's web endpoints are unofficial and undocumented; they can change or be blocked without notice.
public sealed class OpggApi(HttpClient http, TimeSpan? minimumInterval = null, DiagnosticLog? log = null) : IDisposable
{
    public const string SummonerHost = "lol-api-summoner.op.gg";
    public const string ChampionHost = "lol-api-champion.op.gg";
    private const string UserAgent = "RiotAccounts (personal account manager)";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeSpan interval = minimumInterval ?? TimeSpan.FromSeconds(1);
    private DateTimeOffset notBefore;

    public async Task<JsonDocument> GetAsync(string host, string pathAndQuery, CancellationToken ct, IProgress<string>? progress = null)
    {
        if (host is not (SummonerHost or ChampionHost)) throw new ArgumentException("OP.GGの接続先が不正です。", nameof(host));
        if (!pathAndQuery.StartsWith("/api/", StringComparison.Ordinal) || pathAndQuery.Contains('\\') || pathAndQuery.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("OP.GGのパスが不正です。", nameof(pathAndQuery));
        await gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var wait = notBefore - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://{host}{pathAndQuery}"));
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                using var response = await Send(request, host, ApiRoute.Redact(pathAndQuery), ct);
                notBefore = DateTimeOffset.UtcNow + interval;
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                    if (retry < TimeSpan.FromSeconds(1)) retry = TimeSpan.FromSeconds(1);
                    if (retry > TimeSpan.FromMinutes(2)) break;
                    notBefore = DateTimeOffset.UtcNow + retry;
                    progress?.Report($"OP.GGの取得制限です。{Math.Ceiling(retry.TotalSeconds)}秒後に再試行します。");
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new RiotApiException("OP.GGに対象データが見つかりません。Riot ID・タグ・サーバーを確認してください。", response.StatusCode);
                if (!response.IsSuccessStatusCode)
                    throw new RiotApiException($"OP.GGから取得できませんでした（HTTP {(int)response.StatusCode}）。仕様変更の可能性があります。設定でRiot APIに切り替えられます。", response.StatusCode);
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            throw new RiotApiException("OP.GGの取得制限が続いています。時間をおいて更新してください。", HttpStatusCode.TooManyRequests);
        }
        catch (JsonException) { throw new RiotApiException("OP.GGの応答を読み取れませんでした。保存済みデータを表示しています。"); }
        finally { gate.Release(); }
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, string host, string route, CancellationToken ct)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var response = await http.SendAsync(request, ct);
            log?.Write("OP.GG", $"{host} {route} -> HTTP {(int)response.StatusCode} ({timer.ElapsedMilliseconds}ms)");
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            log?.Write("OP.GG", $"{host} {route} -> {(ct.IsCancellationRequested ? "中止" : "通信失敗 " + ex.GetType().Name)} ({timer.ElapsedMilliseconds}ms)");
            throw;
        }
    }

    public void Dispose() => gate.Dispose();
}

public static class OpggRegions
{
    private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["JP1"]="jp", ["KR"]="kr", ["NA1"]="na", ["BR1"]="br", ["LA1"]="lan", ["LA2"]="las", ["EUW1"]="euw", ["EUN1"]="eune",
        ["TR1"]="tr", ["RU"]="ru", ["ME1"]="me", ["OC1"]="oce", ["SG2"]="sg", ["TW2"]="tw", ["VN2"]="vn"
    };
    public static string Region(string platform) => Map.TryGetValue(platform, out var value) ? value : throw new ArgumentException("OP.GGが対応していないサーバーです。");
    public static Uri ProfileUrl(GameProfile profile) =>
        new($"https://op.gg/lol/summoners/{Region(profile.Platform)}/{Uri.EscapeDataString(profile.GameName)}-{Uri.EscapeDataString(profile.TagLine)}");
}

/// <summary>The account as OP.GG knows it. OP.GG's PUUID is encrypted for OP.GG, so it never equals the one from the user's Riot API key.</summary>
public sealed record OpggIdentity(string Region, string GameName, string TagLine, string SummonerId, string Puuid);

public sealed class OpggStatsProvider(Store store, OpggApi api) : IGameStatsProvider
{
    public const string MatchIdPrefix = "OPGG_";
    private const int PageSize = 20, MaxPages = 10;
    private Dictionary<int, string>? champions;

    public static OpggIdentity? Identity(Store store, Guid accountId) => store.Read<OpggIdentity>("opgg", accountId.ToString());

    private async Task<(RiotAccount Account, OpggIdentity Identity)> Resolve(RiotAccount account, CancellationToken ct, IProgress<string>? progress)
    {
        ct.ThrowIfCancellationRequested();
        var profile = account.Lol;
        var region = OpggRegions.Region(profile.Platform);
        var identity = Identity(store, account.Id);
        if (identity is null || identity.Region != region ||
            !string.Equals(identity.GameName, profile.GameName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identity.TagLine, profile.TagLine, StringComparison.OrdinalIgnoreCase))
        {
            var riotId = Uri.EscapeDataString($"{profile.GameName}#{profile.TagLine}");
            using var json = await api.GetAsync(OpggApi.SummonerHost, $"/api/v3/{region}/summoners?riot_id={riotId}&hl=ja_JP", ct, progress);
            JsonElement match;
            try
            {
                // The search is fuzzy, so only an exact Riot ID match identifies the account.
                match = json.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault(s =>
                    string.Equals(Str(s, "game_name"), profile.GameName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Str(s, "tagline"), profile.TagLine, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception error) when (IsShapeError(error)) { throw ShapeError(); }
            if (match.ValueKind != JsonValueKind.Object || Str(match, "summoner_id").Length == 0 || Str(match, "puuid").Length == 0)
                throw new RiotApiException("OP.GGでアカウントが見つかりません。Riot ID・タグ・サーバーを確認してください。", HttpStatusCode.NotFound);
            identity = new(region, profile.GameName, profile.TagLine, Str(match, "summoner_id"), Str(match, "puuid"));
            ct.ThrowIfCancellationRequested();
            store.Write("opgg", account.Id.ToString(), identity);
        }
        if (string.IsNullOrEmpty(profile.Puuid))
        {
            // Stats are keyed by the account's PUUID; LolStatsProvider replaces this one when the Riot API is used later.
            account = account with { Profiles = account.Profiles.Select(p => p.Game == "lol" ? p with { Puuid = identity.Puuid } : p).ToList() };
            store.Save(account);
        }
        return (account, identity);
    }

    public async Task RefreshRanksAsync(RiotAccount account, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        var (resolved, identity) = await Resolve(account, cancellationToken, progress);
        using var json = await api.GetAsync(OpggApi.SummonerHost, $"/api/{identity.Region}/summoners/{Uri.EscapeDataString(identity.SummonerId)}/summary?hl=ja_JP", cancellationToken, progress);
        List<RankEntry> ranks;
        try { ranks = ParseRanks(json.RootElement); }
        catch (Exception error) when (IsShapeError(error)) { throw ShapeError(); }
        cancellationToken.ThrowIfCancellationRequested();
        var cache = store.Cache(resolved.Id);
        cache.Ranks.Add(new(DateTimeOffset.UtcNow, ranks));
        store.SaveCache(resolved.Id, cache);
    }

    public async Task RefreshAnalysisAsync(RiotAccount account, string queue, int count, IProgress<string> progress, CancellationToken cancellationToken)
    {
        if (count is not (20 or 50)) throw new ArgumentOutOfRangeException(nameof(count));
        var definition = Queues.Get(queue);
        progress.Report("OP.GGからアカウントを確認中…");
        if (definition.IsRanked) await RefreshRanksAsync(account, cancellationToken, progress);
        var (resolved, identity) = await Resolve(store.Accounts().SingleOrDefault(a => a.Id == account.Id) ?? account, cancellationToken, progress);
        var self = resolved.Lol.Puuid!;
        var names = await Champions(cancellationToken, progress);
        var cache = store.Cache(resolved.Id);
        var known = cache.Matches.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var gameType = queue switch { Queues.Solo => "SOLORANKED", Queues.Flex => "FLEXRANKED", _ => "TOTAL" };
        string? endedAt = null;
        var found = 0;
        for (var page = 0; page < MaxPages && found < count; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report($"OP.GGから試合履歴を取得中（{page + 1}ページ目）…");
            var path = $"/api/{identity.Region}/summoners/{Uri.EscapeDataString(identity.SummonerId)}/games?limit={PageSize}&game_type={gameType}&hl=ja_JP";
            if (endedAt != null) path += $"&ended_at={Uri.EscapeDataString(endedAt)}";
            using var json = await api.GetAsync(OpggApi.SummonerHost, path, cancellationToken, progress);
            List<(MatchRecord Match, string CreatedAt)> games;
            try
            {
                games = json.RootElement.GetProperty("data").EnumerateArray()
                    .Select(g => (ParseGame(g, identity.Puuid, self, names), Str(g, "created_at"))).ToList();
            }
            catch (Exception error) when (IsShapeError(error)) { throw ShapeError(); }
            foreach (var (match, _) in games)
            {
                if (!definition.QueueIds.Contains(match.QueueId)) continue;
                if (match.Participants.Count(p => p.Puuid == self) != 1)
                    throw new RiotApiException("OP.GGの試合にアカウントが含まれていません。保存済みデータを表示しています。");
                if (known.Add(match.Id)) cache.Matches.Add(match);
                if (!match.Remake && match.DurationSeconds > 0) found++;
                if (found >= count) break;
            }
            cancellationToken.ThrowIfCancellationRequested();
            store.SaveCache(resolved.Id, cache);
            if (games.Count < PageSize || games[^1].CreatedAt.Length == 0 || games[^1].CreatedAt == endedAt) break;
            endedAt = games[^1].CreatedAt;
        }
        var now = DateTimeOffset.UtcNow;
        cache.MatchesUpdatedAt = now;
        cache.QueueUpdatedAt[queue] = now;
        store.SaveCache(resolved.Id, cache);
        progress.Report(definition.IsRanked
            ? $"OP.GGから戦績を更新しました（{found}/{count}戦）。参考ランク帯はRiot API利用時のみ算出します。"
            : $"OP.GGからノーマルの戦績を更新しました（{found}/{count}戦）。");
    }

    private async Task<Dictionary<int, string>> Champions(CancellationToken ct, IProgress<string> progress)
    {
        if (champions != null) return champions;
        using var json = await api.GetAsync(OpggApi.ChampionHost, "/api/meta/champions?hl=en_US", ct, progress);
        try
        {
            // The English key matches Riot's championName, so both sources group champions identically.
            champions = json.RootElement.GetProperty("data").EnumerateArray()
                .Select(c => (Id: c.GetProperty("id").GetInt32(), Key: Str(c, "key")))
                .Where(c => c.Key.Length > 0).DistinctBy(c => c.Id).ToDictionary(c => c.Id, c => c.Key);
        }
        catch (Exception error) when (IsShapeError(error)) { throw ShapeError(); }
        return champions;
    }

    public static List<RankEntry> ParseRanks(JsonElement root)
    {
        var ranks = new List<RankEntry>();
        foreach (var stat in root.GetProperty("data").GetProperty("summoner").GetProperty("league_stats").EnumerateArray())
        {
            var queue = Str(stat, "game_type") switch { "SOLORANKED" => Queues.Solo, "FLEXRANKED" => Queues.Flex, _ => null };
            if (queue is null || !stat.TryGetProperty("tier_info", out var tier) || Str(tier, "tier").Length == 0) continue;
            var division = Num(tier, "division") switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", _ => "" };
            ranks.Add(new(queue, Str(tier, "tier").ToUpperInvariant(), division, Num(tier, "lp"), Num(stat, "win"), Num(stat, "lose")));
        }
        return ranks.DistinctBy(r => r.QueueType).ToList();
    }

    /// <param name="opggPuuid">The account's PUUID as OP.GG reports it.</param>
    /// <param name="selfPuuid">The PUUID stats are keyed by; the account's own participant is rewritten to it.</param>
    public static MatchRecord ParseGame(JsonElement game, string opggPuuid, string selfPuuid, IReadOnlyDictionary<int, string> champions)
    {
        var participants = game.GetProperty("participants").EnumerateArray().Select(p =>
        {
            var stats = p.GetProperty("stats");
            var hasSummoner = p.TryGetProperty("summoner", out var summoner);
            var puuid = hasSummoner ? Str(summoner, "puuid") : "";
            var champion = champions.TryGetValue(Num(p, "champion_id"), out var name) ? name : $"#{Num(p, "champion_id")}";
            return new Participant(puuid == opggPuuid ? selfPuuid : puuid, Str(p, "team_key") == "RED" ? 200 : 100, champion,
                Role(Str(p, "position")), Num(stats, "kill"), Num(stats, "death"), Num(stats, "assist"),
                Num(stats, "minion_kill") + Num(stats, "neutral_minion_kill"), Num(stats, "vision_score"), Str(stats, "result") == "WIN",
                hasSummoner ? Num(summoner, "level") : 0);
        }).ToList();
        var id = Str(game, "id");
        if (id.Length == 0) throw new FormatException("OP.GG game id is missing.");
        return new(MatchIdPrefix + id, Num(game, "queue_id"),
            DateTimeOffset.Parse(Str(game, "created_at"), CultureInfo.InvariantCulture), Num(game, "game_length_second"),
            game.TryGetProperty("is_remake", out var remake) && remake.ValueKind == JsonValueKind.True, participants);
    }

    // Riot's teamPosition names, so role stats merge across sources.
    private static string Role(string position) => position switch
    {
        "TOP" => "TOP", "JUNGLE" => "JUNGLE", "MID" => "MIDDLE", "ADC" => "BOTTOM", "SUPPORT" => "UTILITY", _ => ""
    };

    private static bool IsShapeError(Exception error) =>
        error is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException;
    private static RiotApiException ShapeError() => new("OP.GGの応答形式が想定と異なります。仕様変更の可能性があります。設定でRiot APIに切り替えられます。");
    private static string Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int Num(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
