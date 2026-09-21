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
