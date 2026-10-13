using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RiotAccounts.Core;

// Fake web server and sample records shared by the web client and import tests. Nothing here resembles a real secret.
static class WebTestData
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly DateTimeOffset At = new(2026, 10, 12, 3, 0, 0, TimeSpan.Zero);

    public static HttpResponseMessage Reply(HttpStatusCode code, object body) => new(code) { Content = JsonContent.Create(body, body.GetType(), options: Json) };
    public static RiotAccount Account(string label = "Main", string gameName = "Name", string? puuid = "local") =>
        new(Guid.NewGuid(), label, [new("lol", gameName, "JP1", "JP1", puuid)]);
    public static MatchRecord Match(string id, string puuid, DateTimeOffset? started = null) =>
        new(id, 420, started ?? At, 1800, false, [new(puuid, 100, "Ahri", "MIDDLE", 1, 2, 3, 100, 10, true)]);
    public static WebAccount Remote(RiotAccount account, string puuid = "remote", string? gameName = null)
    {
        var cache = new AccountCache { MatchesUpdatedAt = At };
        cache.Ranks.Add(new(At, [new(Queues.Solo, "GOLD", "II", 40, 10, 8)]));
        cache.Matches.Add(Match("JP1_2", puuid));
        return new(account.Id, account.Label, account.Note, account.Lol with { Puuid = puuid, GameName = gameName ?? account.Lol.GameName }, cache);
    }
    public static WebJob Job(Guid id, string state, string? progress = null, string? message = null) => new(id, state, progress, message, null, Queues.Solo, false, At, null, null);
    public static WebStatus Status(RefreshResult? lastRun = null, WebJob? current = null, DateTimeOffset? changedAt = null) =>
        new("1.4.0", true, current, 0, lastRun, At.AddHours(6), 6, [], changedAt);
}

sealed record WebCall(string Method, string Path, string? Body);

sealed class WebTestServer : IDisposable
{
    private readonly Handler handler;
    public List<WebCall> Calls { get; } = [];
    public Func<HttpRequestMessage, HttpResponseMessage> Route { get; set; } = _ => new(HttpStatusCode.NotFound);
    public HttpClient Http { get; }
    public WebTestServer()
    {
        handler = new(async (request, ct) =>
        {
            Calls.Add(new(request.Method.Method, request.RequestUri!.AbsolutePath, request.Content == null ? null : await request.Content.ReadAsStringAsync(ct)));
            return Route(request);
        });
        Http = new(handler);
    }
    public void Dispose() { Http.Dispose(); handler.Dispose(); }
}

sealed class Reports : IProgress<string> { public List<string> Values { get; } = []; public void Report(string value) => Values.Add(value); }
