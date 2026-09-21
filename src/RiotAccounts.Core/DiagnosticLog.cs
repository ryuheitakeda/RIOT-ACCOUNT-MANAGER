namespace RiotAccounts.Core;

/// <summary>
/// Append-only troubleshooting log. Callers pass fixed stage names and messages only; credentials, keys,
/// PUUIDs and field contents must never be passed in. Logging failures are swallowed so they cannot break the app.
/// </summary>
public sealed class DiagnosticLog(string filePath, long maxBytes = 256 * 1024)
{
    private readonly object gate = new();
    public string FilePath { get; } = filePath;

    public void Write(string stage, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Clean(stage)}] {Clean(message)}{Environment.NewLine}";
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > maxBytes) File.Move(FilePath, FilePath + ".1", true);
                File.AppendAllText(FilePath, line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string Clean(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
}

/// <summary>Reduces an API path to its route template so logs never contain PUUIDs, Riot IDs, match IDs or query values.</summary>
public static class ApiRoute
{
    private static readonly HashSet<string> Literals = new(StringComparer.Ordinal)
    {
        "riot", "account", "v1", "accounts", "by-riot-id", "lol", "league", "v4", "entries", "by-puuid", "match", "v5", "matches", "ids",
        "api", "v3", "summoners", "summary", "games", "meta", "champions",
        "jp", "kr", "na", "br", "lan", "las", "euw", "eune", "tr", "ru", "me", "oce", "sg", "tw", "vn"
    };

    public static string Redact(string pathAndQuery)
    {
        var path = pathAndQuery.Split('?')[0];
        return string.Join('/', path.Split('/').Select(s => s.Length == 0 || Literals.Contains(s) ? s : "{id}"));
    }
}
