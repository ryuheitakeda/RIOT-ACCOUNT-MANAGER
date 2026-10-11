using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RiotAccounts.Core;

namespace RiotAccounts.Web.Pages;

// Only the non-secret fields; login IDs and passwords stay in the WPF app.
public sealed class AccountsModel(Store store, RefreshWorker worker) : PageModel
{
    public sealed class Input
    {
        public string Label { get; set; } = "";
        public string GameName { get; set; } = "";
        public string TagLine { get; set; } = "";
        public string Platform { get; set; } = "JP1";
        public string? Note { get; set; }
    }

    [BindProperty(SupportsGet = true)] public Guid? Edit { get; set; }
    [BindProperty] public Input Form { get; set; } = new();
    public List<RiotAccount> Accounts { get; private set; } = [];
    public string? Error { get; private set; }
    public static IEnumerable<string> Platforms => Regions.Routing.Keys;

    public void OnGet()
    {
        Accounts = store.Accounts();
        if (Edit is { } id && Accounts.SingleOrDefault(a => a.Id == id) is { } account)
            Form = new() { Label = account.Label, GameName = account.Lol.GameName, TagLine = account.Lol.TagLine, Platform = account.Lol.Platform.ToUpperInvariant(), Note = account.Note };
        else Edit = null;
    }

    public IActionResult OnPostSave()
    {
        var adding = Edit == null;
        try { AccountEdits.Save(store, worker, Edit ?? Guid.NewGuid(), new(Form.Label, Form.GameName, Form.TagLine, Form.Platform, Form.Note)); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            Error = error.Message;
            Accounts = store.Accounts();
            return Page();
        }
        TempData["Message"] = adding ? $"「{Form.Label.Trim()}」を追加しました。次の自動更新か「今すぐ全件更新」で戦績を取得します。" : $"「{Form.Label.Trim()}」を保存しました。";
        return RedirectToPage(new { edit = (Guid?)null });
    }

    public IActionResult OnPostDelete(Guid id)
    {
        var label = store.Accounts().SingleOrDefault(a => a.Id == id)?.Label;
        try { AccountEdits.Delete(store, worker, id); TempData["Message"] = $"「{label}」を削除しました。"; }
        catch (InvalidOperationException error) { TempData["Message"] = error.Message; }
        return RedirectToPage(new { edit = (Guid?)null });
    }

    public IActionResult OnPostMove(Guid id, bool up)
    {
        var ids = store.Accounts().Select(a => a.Id).ToList();
        var index = ids.IndexOf(id);
        var neighbour = up ? index - 1 : index + 1;
        if (index >= 0 && neighbour >= 0 && neighbour < ids.Count) store.MoveAccount(id, ids[neighbour], after: !up);
        return RedirectToPage(new { edit = (Guid?)null });
    }
}
