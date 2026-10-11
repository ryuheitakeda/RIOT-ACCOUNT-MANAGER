using System.Text.Json.Serialization;

namespace RiotAccounts.Core;

// The JSON contract between the web server and its clients (the WPF "Web" source), serialized with JsonSerializerDefaults.Web like Store.
// Nothing here carries login credentials or the Riot API key.

public sealed record WebAccount(Guid Id, string Label, string? Note, GameProfile Lol, AccountCache Cache);

public sealed record WebSnapshot(DateTimeOffset GeneratedAt, List<WebAccount> Accounts);

// The fields a client may create or change. Login IDs and passwords stay on the client.
public sealed record WebAccountInput(string Label, string GameName, string TagLine, string Platform, string? Note);

// Count null uses the server's default (RefreshCycle.DefaultCount).
public sealed record WebRefreshRequest(Guid? AccountId = null, string? Queue = null, int? Count = null, bool RanksOnly = false);

public sealed record WebJob(Guid Id, string State, string? Progress, string? Message, Guid? AccountId, string? Queue, bool RanksOnly,
    DateTimeOffset QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt)
{
    public const string Queued = "queued", Running = "running", Succeeded = "succeeded", Failed = "failed";
    [JsonIgnore] public bool Finished => State is Succeeded or Failed;
}

public sealed record WebStatus(string Version, bool KeyRegistered, WebJob? Current, int Queued, RefreshResult? LastRun,
    DateTimeOffset NextDue, double IntervalHours, List<RateLimitObservation> RateLimits);

// Error body of every failed API call; the message is Japanese and ready to show.
public sealed record WebError(string Message);
