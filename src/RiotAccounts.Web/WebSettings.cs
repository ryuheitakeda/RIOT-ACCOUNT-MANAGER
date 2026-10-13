using RiotAccounts.Core;

namespace RiotAccounts.Web;

// Bound from the "RiotWeb" section of appsettings.json or from RiotWeb__* environment variables (the launchd plist sets them).
public sealed record WebSettings
{
    public const string Section = "RiotWeb";
    public string DataDir { get; init; } = "data";
    public double RefreshIntervalHours { get; init; } = RefreshSchedule.DefaultInterval.TotalHours;
    public int MatchCount { get; init; } = RefreshCycle.DefaultCount;
    public TimeSpan Interval => TimeSpan.FromHours(RefreshIntervalHours);

    public WebSettings Validated(string contentRoot)
    {
        // A short interval only spends the key's budget on unchanged data; the provider supports 20 or 50 matches.
        if (RefreshIntervalHours < 1) throw new InvalidOperationException("RiotWeb:RefreshIntervalHours は1以上にしてください。");
        if (MatchCount is not (20 or 50)) throw new InvalidOperationException("RiotWeb:MatchCount は20か50にしてください。");
        return this with { DataDir = Path.GetFullPath(DataDir, contentRoot) };
    }
}
