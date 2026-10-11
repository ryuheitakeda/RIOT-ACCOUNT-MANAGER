using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.WebEncoders;
using RiotAccounts.Core;
using RiotAccounts.Web;

var builder = WebApplication.CreateBuilder(args);
var settings = (builder.Configuration.GetSection(WebSettings.Section).Get<WebSettings>() ?? new()).Validated(builder.Environment.ContentRootPath);
Directory.CreateDirectory(settings.DataDir);

builder.Services.AddSingleton(settings);
builder.Services.AddDataProtection()
    .SetApplicationName("RiotAccounts.Web")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.DataDir, "keys")));
builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddSingleton(sp => new Store(Path.Combine(settings.DataDir, "accounts.db"), sp.GetRequiredService<ISecretProtector>()));
builder.Services.AddSingleton(_ => new DiagnosticLog(Path.Combine(settings.DataDir, "logs", "diagnostics.log")));
builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
builder.Services.AddSingleton<ApiKey>();
builder.Services.AddSingleton(sp => new RiotApi(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ApiKey>().Get, sp.GetRequiredService<DiagnosticLog>()));
builder.Services.AddSingleton(sp => new LolStatsProvider(sp.GetRequiredService<Store>(), sp.GetRequiredService<RiotApi>()));
// StatsDiagnostics needs an OP.GG client, but the server only ever runs the Riot API checks, so it never sends a request.
builder.Services.AddSingleton(sp => new OpggApi(sp.GetRequiredService<HttpClient>(), null, sp.GetRequiredService<DiagnosticLog>()));
builder.Services.AddSingleton(sp => new StatsDiagnostics(sp.GetRequiredService<RiotApi>(), sp.GetRequiredService<OpggApi>(),
    sp.GetRequiredService<ApiKey>().Get, sp.GetRequiredService<DiagnosticLog>()));
builder.Services.AddSingleton<RefreshWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RefreshWorker>());
builder.Services.AddRazorPages();
// Japanese text stays readable in the HTML instead of becoming numeric character references.
builder.Services.Configure<WebEncoderOptions>(o => o.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
builder.Services.AddResponseCompression();

var app = builder.Build();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
app.UseResponseCompression();
app.UseStaticFiles();
app.MapRazorPages();
app.MapApi();
app.Run();
