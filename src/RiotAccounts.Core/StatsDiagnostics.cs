using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace RiotAccounts.Core;

public sealed record DiagnosticStep(string Name, bool Ok, string Detail, long Milliseconds);

public sealed record DiagnosticReport(string Source, string Account, IReadOnlyList<DiagnosticStep> Steps, IReadOnlyList<string> Skipped)
{
    public bool Ok => Steps.All(s => s.Ok);
    public DiagnosticStep? Failed => Steps.FirstOrDefault(s => !s.Ok);

    public string ToText()
    {
        var text = new StringBuilder($"取得元: {Source}\nアカウント: {Account}\n\n");
        foreach (var step in Steps) text.AppendLine($"[{(step.Ok ? "OK" : "NG")}] {step.Name}（{step.Milliseconds}ms）\n     {step.Detail}");
        foreach (var name in Skipped) text.AppendLine($"[--] {name}\n     前の段階が失敗したため未実行");
        return text.ToString().TrimEnd();
    }
}

/// <summary>
/// Read-only connection test, one stage at a time (key → account → rank → match list → one match), stopping at the first failure.
/// Nothing is saved and no key, PUUID or response body is shown or logged.
/// </summary>
public sealed class StatsDiagnostics(RiotApi riot, OpggApi opgg, Func<string?> getRiotKey, DiagnosticLog? log = null)
{
    private sealed record Stage(string Name, Func<Task<string>> Run);

    public Task<DiagnosticReport> RunAsync(RiotAccount account, string source, IProgress<string> progress, CancellationToken ct) =>
        source == "opgg" ? RunOpgg(account, progress, ct) : RunRiot(account, progress, ct);

    private Task<DiagnosticReport> RunRiot(RiotAccount account, IProgress<string> progress, CancellationToken ct)
    {
        var profile = account.Lol;
        string? puuid = null, matchId = null;
        return Execute("Riot API（公式）", account, progress, ct,
        [
            new("APIキー", () =>
            {
                if (string.IsNullOrWhiteSpace(getRiotKey())) throw new RiotApiException("Riot APIキーが未登録です。設定で登録してください。");
                return Task.FromResult("登録済み（値は表示しません）。開発用キーは24時間で失効します。");
            }),
            new("Riot IDの解決", async () =>
            {
                using var json = await riot.GetAsync<JsonDocument>(Regions.AccountRegional(profile.Platform),
                    $"/riot/account/v1/accounts/by-riot-id/{Uri.EscapeDataString(profile.GameName)}/{Uri.EscapeDataString(profile.TagLine)}", ct);
                puuid = Text(json.RootElement, "puuid");
                if (puuid.Length == 0) throw new RiotApiException("応答にPUUIDが含まれていません。");
                var note = !string.IsNullOrEmpty(profile.Puuid) && profile.Puuid != puuid
                    ? "保存済みのPUUIDと一致しません（別のキー／OP.GG由来）。更新時に再取得します。" : "PUUIDを取得できました。";
                return note;
            }),
            new("ランク取得", async () =>
            {
                var entries = await riot.GetAsync<List<RankEntry>>(profile.Platform.ToLowerInvariant(), $"/lol/league/v4/entries/by-puuid/{Uri.EscapeDataString(puuid!)}", ct);
                return $"{entries.Count}件のランク情報を取得できました。";
            }),
            new("試合一覧", async () =>
            {
                var ids = await riot.GetAsync<List<string>>(Regions.Regional(profile.Platform), $"/lol/match/v5/matches/by-puuid/{Uri.EscapeDataString(puuid!)}/ids?count=1", ct);
                matchId = ids.FirstOrDefault();
                return matchId == null ? "取得できましたが、試合がありません。" : "直近の試合IDを取得できました。";
            }),
            new("試合詳細", async () =>
            {
                if (matchId == null) return "試合がないため確認を省略しました。";
                using var json = await riot.GetAsync<JsonDocument>(Regions.Regional(profile.Platform), $"/lol/match/v5/matches/{Uri.EscapeDataString(matchId)}", ct);
                return json.RootElement.TryGetProperty("info", out _) ? "試合詳細を取得できました。" : throw new RiotApiException("応答の形式が想定と異なります（info がありません）。");
            })
        ]);
    }

    private Task<DiagnosticReport> RunOpgg(RiotAccount account, IProgress<string> progress, CancellationToken ct)
    {
        var profile = account.Lol;
        string region = "", summoner = "";
        return Execute("OP.GG（非公式）", account, progress, ct,
        [
            new("アカウント検索", async () =>
            {
                region = OpggRegions.Region(profile.Platform);
                var riotId = Uri.EscapeDataString($"{profile.GameName}#{profile.TagLine}");
                using var json = await opgg.GetAsync(OpggApi.SummonerHost, $"/api/v3/{region}/summoners?riot_id={riotId}&hl=ja_JP", ct);
                var match = json.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault(s =>
                    string.Equals(Text(s, "game_name"), profile.GameName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Text(s, "tagline"), profile.TagLine, StringComparison.OrdinalIgnoreCase));
                summoner = match.ValueKind == JsonValueKind.Object ? Text(match, "summoner_id") : "";
                if (summoner.Length == 0) throw new RiotApiException("OP.GGにこのRiot IDが見つかりません。Riot ID・タグ・サーバーを確認してください。", HttpStatusCode.NotFound);
                return "OP.GGでアカウントを確認できました。";
            }),
            new("ランク（summary）", async () =>
            {
                using var json = await opgg.GetAsync(OpggApi.SummonerHost, $"/api/{region}/summoners/{Uri.EscapeDataString(summoner)}/summary?hl=ja_JP", ct);
                return json.RootElement.TryGetProperty("data", out _) ? "サマリーを取得できました。" : throw new RiotApiException("応答の形式が想定と異なります（data がありません）。OP.GG側の仕様変更の可能性があります。");
            }),
            new("試合履歴（games）", async () =>
            {
                using var json = await opgg.GetAsync(OpggApi.SummonerHost, $"/api/{region}/summoners/{Uri.EscapeDataString(summoner)}/games?limit=1&game_type=TOTAL&hl=ja_JP", ct);
                return $"{json.RootElement.GetProperty("data").GetArrayLength()}件の試合を取得できました。";
            }),
            new("チャンピオン情報", async () =>
            {
                using var json = await opgg.GetAsync(OpggApi.ChampionHost, "/api/meta/champions?hl=en_US", ct);
                return $"{json.RootElement.GetProperty("data").GetArrayLength()}件のチャンピオン情報を取得できました。";
            })
        ]);
    }

    private async Task<DiagnosticReport> Execute(string source, RiotAccount account, IProgress<string> progress, CancellationToken ct, Stage[] stages)
    {
        var steps = new List<DiagnosticStep>();
        foreach (var stage in stages)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report($"診断中：{stage.Name}…");
            var timer = Stopwatch.StartNew();
            DiagnosticStep step;
            try { step = new(stage.Name, true, await stage.Run(), timer.ElapsedMilliseconds); }
            catch (RiotApiException ex) { step = new(stage.Name, false, (ex.StatusCode is { } code ? $"HTTP {(int)code}: " : "") + ex.Message, timer.ElapsedMilliseconds); }
            catch (HttpRequestException ex) { step = new(stage.Name, false, $"通信できません（{ex.GetType().Name}）。接続・プロキシ・セキュリティソフトを確認してください。", timer.ElapsedMilliseconds); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { step = new(stage.Name, false, "応答がありませんでした（タイムアウト）。", timer.ElapsedMilliseconds); }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
            { step = new(stage.Name, false, $"応答を解釈できません（{ex.GetType().Name}）。取得元の仕様変更の可能性があります。", timer.ElapsedMilliseconds); }
            steps.Add(step);
            log?.Write("診断", $"{source} {stage.Name}: {(step.Ok ? "OK" : "NG")} {step.Detail}");
            if (!step.Ok) break;
        }
        var skipped = stages.Skip(steps.Count).Select(s => s.Name).ToList();
        return new(source, $"{account.Label}（{account.RiotId}）", steps, skipped);
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

/// <summary>Logs the start, outcome, duration and error type of each refresh; never the account, key or response data.</summary>
public sealed class LoggedStatsProvider(IGameStatsProvider inner, string source, DiagnosticLog log) : IGameStatsProvider
{
    public Task RefreshRanksAsync(RiotAccount account, CancellationToken cancellationToken, IProgress<string>? progress = null) =>
        Run("ランク更新", () => inner.RefreshRanksAsync(account, cancellationToken, progress));

    public Task RefreshAnalysisAsync(RiotAccount account, string queue, int count, IProgress<string> progress, CancellationToken cancellationToken) =>
        Run($"戦績更新（{queue}・{count}戦）", () => inner.RefreshAnalysisAsync(account, queue, count, progress, cancellationToken));

    private async Task Run(string operation, Func<Task> action)
    {
        var timer = Stopwatch.StartNew();
        log.Write("更新", $"{source} {operation} 開始");
        try { await action(); log.Write("更新", $"{source} {operation} 成功（{timer.ElapsedMilliseconds}ms）"); }
        catch (OperationCanceledException) { log.Write("更新", $"{source} {operation} 中止"); throw; }
        catch (RiotApiException ex) { log.Write("更新", $"{source} {operation} 失敗 {(ex.StatusCode is { } code ? "HTTP " + (int)code : "RiotApiException")}: {ex.Message}"); throw; }
        catch (Exception ex) { log.Write("更新", $"{source} {operation} 失敗 {ex.GetType().Name}"); throw; }
    }
}
