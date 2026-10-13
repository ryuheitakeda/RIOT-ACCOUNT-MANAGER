using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RiotAccounts.Core;

namespace RiotAccounts.Web.Pages;

public sealed class IndexModel(Store store, RefreshWorker worker, ApiKey key, WebSettings settings) : PageModel
{
    public sealed record Row(RiotAccount Account, string Solo, string Flex, string LastPlayed, DateTimeOffset? LastFetched);
    public List<Row> Rows { get; private set; } = [];

    // Built from saved data only; viewing never calls the Riot API.
    public void OnGet()
    {
        var now = DateTimeOffset.Now;
        Rows = [.. store.Accounts().Select(a =>
        {
            var cache = store.Cache(a.Id);
            return new Row(a, Format.Rank(cache, Queues.Solo), Format.Rank(cache, Queues.Flex), Analytics.Overview(cache, now).LastPlayed, Format.LastFetched(cache));
        })];
    }

    public IActionResult OnPostRefreshAll() => Queue(new RefreshTarget(Count: settings.MatchCount), "全アカウントの更新");

    public IActionResult OnPostRefresh(Guid id) =>
        store.Accounts().SingleOrDefault(a => a.Id == id) is { } account ? Queue(new RefreshTarget(id, Count: settings.MatchCount), $"「{account.Label}」の更新") : NotFound();

    private IActionResult Queue(RefreshTarget target, string what)
    {
        if (!key.Registered) TempData["Message"] = "Riot APIキーが未登録のため更新できません。設定画面で登録してください。";
        else { worker.Enqueue(target); TempData["Message"] = $"{what}を予約しました。"; }
        return RedirectToPage();
    }
}
