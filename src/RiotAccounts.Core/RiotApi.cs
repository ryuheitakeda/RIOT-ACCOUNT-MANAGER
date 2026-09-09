using System.Net;
using System.Text.Json;

namespace RiotAccounts.Core;

public sealed class RiotApiException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class RiotApi(HttpClient http, Func<string?> getKey) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Queue<DateTimeOffset> requests = [];
    private DateTimeOffset retryNotBefore;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<T> GetAsync<T>(string host, string path, CancellationToken ct, IProgress<string>? progress = null)
    {
        if (!Regions.Routing.Keys.Contains(host, StringComparer.OrdinalIgnoreCase) &&
            !Regions.Routing.Values.Contains(host, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Riot APIの接続先が不正です。", nameof(host));
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\'))
            throw new ArgumentException("Riot APIのパスが不正です。", nameof(path));
        await gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var key = getKey();
                if (string.IsNullOrWhiteSpace(key)) throw new RiotApiException("設定でRiot APIキーを登録してください。");
                await WaitForRateLimit(ct, progress);
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://{host}.api.riotgames.com{path}"));
                request.Headers.Add("X-Riot-Token", key);
                requests.Enqueue(DateTimeOffset.UtcNow);
                using var response = await http.SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(120);
                    if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
                    retryNotBefore = DateTimeOffset.UtcNow + wait;
                    progress?.Report($"Riot APIの取得制限です。{Math.Ceiling(wait.TotalSeconds)}秒後に再試行します。");
                    ct.ThrowIfCancellationRequested();
                    continue;
                }
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new RiotApiException("APIキーが無効・失効、またはアクセスが許可されていません。設定でキーを確認してください。", response.StatusCode);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new RiotApiException("対象データが見つかりません。Riot ID・タグ・サーバーを確認してください。", response.StatusCode);
                if (!response.IsSuccessStatusCode)
                    throw new RiotApiException($"Riot APIでエラーが発生しました（HTTP {(int)response.StatusCode}）。保存済みデータを表示しています。", response.StatusCode);
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct) ?? throw new RiotApiException("Riot APIから空の応答が返りました。");
            }
            throw new RiotApiException("取得制限が続いています。時間をおいて更新してください。", HttpStatusCode.TooManyRequests);
        }
        catch (JsonException) { throw new RiotApiException("Riot APIの応答を読み取れませんでした。保存済みデータを表示しています。"); }
        finally { gate.Release(); }
    }

    private async Task WaitForRateLimit(CancellationToken ct, IProgress<string>? progress)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            while (requests.TryPeek(out var old) && old <= now.AddSeconds(-120)) requests.Dequeue();
            var next = retryNotBefore;
            // A conservative shared budget also works when requests alternate platform and regional hosts.
            if (requests.Count >= 100) next = Max(next, requests.Peek().AddMilliseconds(120100));
            var lastSecond = requests.Where(t => t > now.AddSeconds(-1)).ToList();
            if (lastSecond.Count >= 20) next = Max(next, lastSecond[0].AddMilliseconds(1100));
            if (next <= now) return;
            var delay = next - now;
            progress?.Report($"API制限に合わせて待機中（約{Math.Ceiling(delay.TotalSeconds)}秒）");
            await Task.Delay(delay, ct);
        }
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;
    public void Dispose() => gate.Dispose();
}

public sealed class LolStatsProvider(Store store, RiotApi api) : IGameStatsProvider
{
    private sealed record Identity(string Puuid, string GameName, string TagLine);
    private async Task<RiotAccount> Resolve(RiotAccount account, CancellationToken ct, IProgress<string>? progress)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(account.Lol.Puuid)) return account;
        var profile = account.Lol;
        var identity = await api.GetAsync<Identity>(Regions.AccountRegional(profile.Platform),
            $"/riot/account/v1/accounts/by-riot-id/{Uri.EscapeDataString(profile.GameName)}/{Uri.EscapeDataString(profile.TagLine)}", ct, progress);
        if (string.IsNullOrWhiteSpace(identity.Puuid)) throw new RiotApiException("アカウントのPUUIDを取得できませんでした。");
        // Preserve all game profiles and the user's Riot ID spelling when resolving the stable identifier.
        var updated = account with { Profiles = account.Profiles.Select(p => p.Game == "lol" ? p with { Puuid = identity.Puuid } : p).ToList() };
        ct.ThrowIfCancellationRequested();
        store.Save(updated);
        return updated;
    }

    public async Task RefreshRanksAsync(RiotAccount account, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        account = await Resolve(account, cancellationToken, progress);
        var ranks = await Ranks(account.Lol.Platform, account.Lol.Puuid!, cancellationToken, progress);
        cancellationToken.ThrowIfCancellationRequested();
        var cache = store.Cache(account.Id);
        cache.Ranks.Add(new(DateTimeOffset.UtcNow, ranks));
        store.SaveCache(account.Id, cache);
    }

    private async Task<List<RankEntry>> Ranks(string platform, string puuid, CancellationToken ct, IProgress<string>? progress = null)
    {
        var ranks = await api.GetAsync<List<RankEntry>>(platform.ToLowerInvariant(),
            $"/lol/league/v4/entries/by-puuid/{Uri.EscapeDataString(puuid)}", ct, progress);
        return ranks.Where(r => r.QueueType is Queues.Solo or Queues.Flex).DistinctBy(r => r.QueueType).ToList();
    }

    public async Task RefreshAnalysisAsync(RiotAccount account, string queue, int count, IProgress<string> progress, CancellationToken cancellationToken)
    {
        if (count is not (20 or 50)) throw new ArgumentOutOfRangeException(nameof(count));
        var queueId = Queues.Id(queue);
        progress.Report("アカウントとランクを取得中…");
        await RefreshRanksAsync(account, cancellationToken, progress);
        account = store.Accounts().Single(a => a.Id == account.Id);
        var cache = store.Cache(account.Id);
        var profile = account.Lol;
        var ids = await api.GetAsync<List<string>>(Regions.Regional(profile.Platform),
            $"/lol/match/v5/matches/by-puuid/{Uri.EscapeDataString(profile.Puuid!)}/ids?queue={queueId}&start=0&count={count}", cancellationToken, progress);
        foreach (var (id, index) in ids.Distinct().Select((id, index) => (id, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cache.Matches.Any(m => m.Id == id)) continue;
            progress.Report($"試合履歴を取得中 {index + 1}/{ids.Count}");
            using var json = await api.GetAsync<JsonDocument>(Regions.Regional(profile.Platform),
                $"/lol/match/v5/matches/{Uri.EscapeDataString(id)}", cancellationToken, progress);
            MatchRecord match;
            try { match = ParseMatch(json.RootElement); }
            catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
            {
                throw new RiotApiException("試合データの形式が不正です。保存済みデータを表示しています。");
            }
            if (match.Id != id || match.QueueId != queueId || match.Participants.Count(p => p.Puuid == profile.Puuid) != 1)
                throw new RiotApiException("試合のアカウントまたはキューが一致しません。保存済みデータを表示しています。");
            cancellationToken.ThrowIfCancellationRequested();
            cache.Matches.Add(match);
            store.SaveCache(account.Id, cache);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        cache.MatchesUpdatedAt = now;
        cache.QueueUpdatedAt[queue] = now;
        store.SaveCache(account.Id, cache);
        var recent = Analytics.Recent(cache, profile.Puuid!, queue, int.MaxValue)
            .Where(m => m.StartedAt >= now.AddDays(-30) && m.StartedAt <= now).Take(20);
        var opponents = recent.SelectMany(m => m.Participants.Where(p => p.TeamId != m.Participants.Single(s => s.Puuid == profile.Puuid).TeamId))
            .Select(p => p.Puuid).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        for (var i = 0; i < opponents.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var puuid = opponents[i];
            now = DateTimeOffset.UtcNow;
            if (cache.Opponents.Any(o => o.Puuid == puuid && o.QueueType == queue && o.ObservedAt >= now.AddHours(-24) && o.ObservedAt <= now)) continue;
            progress.Report($"対戦相手の現在ランクを取得中 {i + 1}/{opponents.Count}");
            RankEntry? rank;
            try { rank = (await Ranks(profile.Platform, puuid, cancellationToken, progress)).SingleOrDefault(e => e.QueueType == queue); }
            catch (RiotApiException error) when (error.StatusCode == HttpStatusCode.NotFound) { rank = null; }
            cancellationToken.ThrowIfCancellationRequested();
            cache.Opponents.Add(new(puuid, queue, DateTimeOffset.UtcNow, rank));
            store.SaveCache(account.Id, cache);
        }
        cancellationToken.ThrowIfCancellationRequested();
        cache.Forecasts.Add(Analytics.Predict(cache, profile.Puuid!, queue, DateTimeOffset.UtcNow));
        store.SaveCache(account.Id, cache);
        progress.Report("戦績・分析を更新しました。");
    }

    public static MatchRecord ParseMatch(JsonElement root)
    {
        var info = root.GetProperty("info");
        var participants = info.GetProperty("participants").EnumerateArray().Select(p => new Participant(
            Str(p, "puuid"), Num(p, "teamId"), Str(p, "championName"), Str(p, "teamPosition"), Num(p, "kills"), Num(p, "deaths"), Num(p, "assists"),
            Num(p, "totalMinionsKilled") + Num(p, "neutralMinionsKilled"), Num(p, "visionScore"), Bool(p, "win"))).ToList();
        var duration = Num(info, "gameDuration");
        var early = info.GetProperty("participants").EnumerateArray().Any(p => Bool(p, "gameEndedInEarlySurrender"));
        return new(Str(root.GetProperty("metadata"), "matchId"), Num(info, "queueId"),
            DateTimeOffset.FromUnixTimeMilliseconds(info.GetProperty("gameStartTimestamp").GetInt64()), duration,
            early && duration < 300, participants);
    }
    private static string Str(JsonElement p, string key) => p.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
    private static int Num(JsonElement p, string key) => p.TryGetProperty(key, out var v) ? v.GetInt32() : 0;
    private static bool Bool(JsonElement p, string key) => p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
}
