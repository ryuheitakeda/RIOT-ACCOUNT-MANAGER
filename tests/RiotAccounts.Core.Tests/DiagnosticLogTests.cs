using RiotAccounts.Core;
using Xunit;

public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "riot-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Fact]
    public void Write_AppendsOneLinePerEntryAndCreatesDirectory()
    {
        var path = Path.Combine(directory, "logs", "diagnostics.log");
        var log = new DiagnosticLog(path);
        log.Write("検出", "Edit=2 Password=1");
        log.Write("入力", "line1\r\nline2");
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("[検出] Edit=2 Password=1", lines[0]);
        Assert.Contains("line1  line2", lines[1]);
    }

    [Fact]
    public void Write_RotatesWhenOverLimit()
    {
        var path = Path.Combine(directory, "diagnostics.log");
        var log = new DiagnosticLog(path, 200);
        for (var i = 0; i < 20; i++) log.Write("stage", new string('x', 40));
        Assert.True(File.Exists(path + ".1"));
        Assert.True(new FileInfo(path).Length <= 400);
    }

    [Fact]
    public void Write_DoesNotThrowWhenPathIsUnusable()
    {
        Directory.CreateDirectory(directory);
        var blocker = Path.Combine(directory, "file");
        File.WriteAllText(blocker, "x");
        new DiagnosticLog(Path.Combine(blocker, "sub", "d.log")).Write("s", "m");
    }
}
