using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RiotAccounts.Core;

namespace RiotAccounts.Web.Pages;

// The WPF detail panel (MainWindow.Render, DrawHistory, DrawAverageTiers) as one page, built from saved data only.
public sealed class AccountModel(Store store, RefreshWorker worker, ApiKey key, WebSettings settings) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Queue { get; set; } = Queues.Solo;
    [BindProperty(SupportsGet = true)] public int Count { get; set; } = 20;

    public RiotAccount Account { get; private set; } = null!;
    public AccountCache Cache { get; private set; } = null!;
    public bool Ranked { get; private set; }
    public RankSnapshot? Latest { get; private set; }
    public RankEntry? Rank { get; private set; }
    public Forecast? Forecast { get; private set; }
    public List<MatchRecord> Recent { get; private set; } = [];
    public Performance? Summary { get; private set; }
    public List<Performance> Champions { get; private set; } = [];
    public List<Performance> Roles { get; private set; } = [];
    public string ChartJson { get; private set; } = "";
    public bool HasChart { get; private set; }
    public List<History.RankRow> RankRows { get; private set; } = [];
    public List<History.TierRow> TierRows { get; private set; } = [];
    public string Puuid => Account.Lol.Puuid ?? "";

    public IActionResult OnGet(Guid id)
    {
        if (store.Accounts().SingleOrDefault(a => a.Id == id) is not { } account) return NotFound();
        if (Queues.Definitions.All(q => q.Key != Queue)) Queue = Queues.Solo;
        if (Count is not (20 or 50)) Count = 20;
        Account = account;
        Cache = store.Cache(id);
        Ranked = Queues.IsRanked(Queue);
        Latest = Cache.Ranks.LastOrDefault();
        Rank = Latest?.Entries.FirstOrDefault(e => e.QueueType == Queue);
        Forecast = Cache.Forecasts.LastOrDefault(f => f.QueueType == Queue);
        Recent = Analytics.Recent(Cache, Puuid, Queue, Count);
        Summary = Analytics.Summarize(Recent, Puuid, _ => "全体").FirstOrDefault();
        Champions = Analytics.Summarize(Recent, Puuid, p => p.Champion);
        Roles = Analytics.Summarize(Recent, Puuid, p => Analytics.RoleName(p.Role));
        ChartSpec chart;
        if (Ranked) (chart, RankRows) = History.Ranks(Cache, Queue);
        else (chart, TierRows) = History.AverageTiers(Cache, Puuid, Queue);
        HasChart = chart.Segments.Count > 0;
        ChartJson = Format.Json(chart);
        return Page();
    }

    public IActionResult OnPostRefresh(Guid id)
    {
        if (store.Accounts().SingleOrDefault(a => a.Id == id) is not { } account) return NotFound();
        if (!key.Registered) TempData["Message"] = "Riot APIキーが未登録のため更新できません。設定画面で登録してください。";
        else { worker.Enqueue(new RefreshTarget(id, Count: settings.MatchCount)); TempData["Message"] = $"「{account.Label}」の更新を予約しました。"; }
        return RedirectToPage(new { id, queue = Queue, count = Count });
    }

    public string ForecastDetails()
    {
        if (Forecast is not { } f) return "自動更新で相手ランクを取得します。直近30日の10戦以上が必要です。";
        var missing = f.TotalPlayers == 0 ? 100 : 100.0 * (f.TotalPlayers - f.KnownPlayers) / f.TotalPlayers;
        var text = $"{f.Explanation}\n{f.MatchCount}戦 / 相手 {f.KnownPlayers}/{f.TotalPlayers}件 / 欠測率 {missing:F1}%"
            + (f.EstimatedPlayers > 0 ? $" / うちレベル推定 {f.EstimatedPlayers}件" : "") + $"\n取得 {Format.Time(f.CreatedAt)}"
            + (f.Median == null ? "" : $"\n中央値 {f.Median}");
        if (f.OldestRankObservedAt is { } oldest && f.LatestRankObservedAt is { } newest)
            text += $"\n相手ランク観測 {Format.Short(oldest)} 〜 {Format.Short(newest)}";
        return text;
    }

    public Participant Self(MatchRecord match) => match.Participants.Single(p => p.Puuid == Puuid);
}
