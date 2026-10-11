using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RiotAccounts.Core;

namespace RiotAccounts.Web.Pages;

public sealed class ChampionsModel(Store store) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Queue { get; set; } = Queues.Solo;
    [BindProperty(SupportsGet = true)] public int Count { get; set; } = 20;
    public int AccountCount { get; private set; }
    public List<ChampionAcrossAccounts> Rows { get; private set; } = [];

    public void OnGet()
    {
        if (Queues.Definitions.All(q => q.Key != Queue)) Queue = Queues.Solo;
        if (Count is not (20 or 50)) Count = 20;
        var accounts = store.Accounts();
        AccountCount = accounts.Count;
        Rows = Analytics.ChampionsAcrossAccounts(accounts.Where(a => a.Lol.Puuid != null).Select(a => (a.Label, store.Cache(a.Id), a.Lol.Puuid!)), Queue, Count);
    }
}
