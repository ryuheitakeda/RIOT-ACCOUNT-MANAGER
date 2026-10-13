using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RiotAccounts.Core;

namespace RiotAccounts.Web.Pages;

public sealed class SettingsModel(Store store, ApiKey key, RefreshWorker worker, StatsDiagnostics diagnostics, WebSettings settings) : PageModel
{
    public bool Registered { get; private set; }
    public List<RiotAccount> Accounts { get; private set; } = [];
    public List<RateLimitObservation> RateLimits { get; private set; } = [];
    public WebSettings Settings => settings;
    public string? Report { get; private set; }

    public void OnGet() => Load();

    // The key is only ever written; the page never shows it back.
    public IActionResult OnPostKey(string? apiKey)
    {
        var value = apiKey?.Trim() ?? "";
        if (value.Length == 0) { TempData["Message"] = "APIキーを入力してください。"; return RedirectToPage(); }
        key.Set(value);
        worker.KeyChanged();
        var problems = RiotKeyFormat.Problems(value);
        TempData["Message"] = problems.Count == 0 ? "APIキーを保存しました。" : "APIキーを保存しましたが、形式に注意してください: " + string.Join("／", problems);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDiagnoseAsync(Guid accountId, CancellationToken ct)
    {
        if (store.Accounts().SingleOrDefault(a => a.Id == accountId) is not { } account) return RedirectToPage();
        Report = (await diagnostics.RunAsync(account, "riot", new Progress<string>(), ct)).ToText();
        Load();
        return Page();
    }

    private void Load()
    {
        Registered = key.Registered;
        Accounts = store.Accounts();
        RateLimits = worker.RateLimits();
    }
}
