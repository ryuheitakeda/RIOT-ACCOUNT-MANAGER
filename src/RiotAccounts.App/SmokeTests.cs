using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace RiotAccounts.App;

/// <summary>Offline checks using a temporary database and generated dummy secrets only.</summary>
internal static class SmokeTests
{
    public static string Run(string? artifactDirectory = null)
    {
        var report = new List<string>
        {
            $"Riot Accounts offline Windows checks — {DateTimeOffset.UtcNow:O}",
            $"OS: {Environment.OSVersion.VersionString}; runtime: {Environment.Version}"
        };
        var directory = Path.Combine(Path.GetTempPath(), "RiotAccounts-self-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            Check(report, "DPAPI CurrentUser roundtrip and tamper rejection", TestDpapi);
            Check(report, "SQLite persist/reopen/edit/delete and encrypted database/WAL", () => TestStore(directory));
            Check(report, "WPF main window and account/settings dialogs construct with dummy data", () => TestWindows(directory, artifactDirectory));
            Check(report, "WPF normal match history/statistics and ranked/normal tab switching", () => TestNormalWindows(directory, artifactDirectory));
            Check(report, "Windows INPUT ABI and synthetic calibration image comparison", TestNativeHelpers);
            Check(report, "Calibration rejects size/DPI/version/legacy/bounds changes", TestCalibrationGeometry);
        }
        catch (Exception exception)
        {
            report.Add($"FAIL test setup ({exception.GetType().Name})");
        }
        finally
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception)
            {
                report.Add($"FAIL temporary test data cleanup ({exception.GetType().Name})");
            }
        }
        report.Add("MANUAL: actual Riot login, foreground changes, calibration, DPI changes, clipboard expiry, and live API comparison.");
        return string.Join(Environment.NewLine, report) + Environment.NewLine;
    }

    private static void Check(List<string> report, string name, Action test)
    {
        try { test(); report.Add("PASS " + name); }
        catch (Exception exception) { report.Add($"FAIL {name} ({exception.GetType().Name}): {exception.Message}"); }
    }

    private static void Require(bool condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (!condition) throw new InvalidOperationException("Self-test assertion failed: " + expression);
    }

    private static void TestDpapi()
    {
        var protector = new WindowsProtector();
        var plain = RandomNumberGenerator.GetBytes(64);
        byte[]? restored = null;
        try
        {
            var cipher = protector.Protect(plain);
            Require(!cipher.AsSpan().SequenceEqual(plain));
            restored = protector.Unprotect(cipher);
            Require(restored.AsSpan().SequenceEqual(plain));
            cipher[cipher.Length / 2] ^= 0x40;
            try
            {
                var unexpected = protector.Unprotect(cipher);
                CryptographicOperations.ZeroMemory(unexpected);
                throw new InvalidOperationException("Modified DPAPI data was accepted.");
            }
            catch (CryptographicException) { }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (restored != null) CryptographicOperations.ZeroMemory(restored);
        }
    }

    private static void TestStore(string directory)
    {
        var database = Path.Combine(directory, "accounts.db");
        var protector = new WindowsProtector();
        var store = new Store(database, protector);
        var marker = Guid.NewGuid().ToString("N");
        var credentials = new Credentials("dummy-user-" + marker, "dummy-password-" + marker);
        var replacement = new Credentials("dummy-new-user-" + marker, "dummy-new-password-" + marker);
        var apiKey = "dummy-api-key-" + marker;
        var account = new RiotAccount(Guid.NewGuid(), "検証アカウント", [new("lol", "Dummy Player", "JP1", "JP1")]);

        // Keep an independent connection alive so the WAL can be inspected before checkpoint/cleanup.
        using (var observer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            observer.Open();
            using (var keepWal = observer.CreateCommand())
            {
                // Opening SQLite alone does not initialize its pager. Hold a read snapshot
                // so the real writes remain in WAL even when Store disables pooling.
                keepWal.CommandText = "BEGIN; SELECT COUNT(*) FROM documents;";
                _ = keepWal.ExecuteScalar();
            }
            store.Save(account, credentials);
            store.SetSecret("riot-api-key", apiKey);
            store.Write("cache", account.Id.ToString(), new AccountCache { MatchesUpdatedAt = DateTimeOffset.UtcNow });
            SqliteConnection.ClearAllPools();
            var reopened = new Store(database, protector);
            var loaded = reopened.Accounts().Single();
            Require(loaded.Id == account.Id && loaded.Label == account.Label && loaded.Lol == account.Lol);
            Require(reopened.Credentials(account.Id) == credentials);
            Require(reopened.GetSecret("riot-api-key") == apiKey);
            Require(reopened.Cache(account.Id).MatchesUpdatedAt != null);

            var edited = account with { Label = "編集したアカウント" };
            reopened.Save(edited);
            Require(reopened.Accounts().Single().Label == edited.Label);
            Require(reopened.Credentials(account.Id) == credentials);
            reopened.Save(edited, replacement);
            Require(new Store(database, protector).Credentials(account.Id) == replacement);

            var files = Directory.GetFiles(directory, "accounts.db*");
            Require(files.Any(file => file.EndsWith("-wal", StringComparison.Ordinal)));
            foreach (var file in files)
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var bytes = buffer.ToArray();
                foreach (var secret in new[] { credentials.Username, credentials.Password, replacement.Username, replacement.Password, apiKey })
                {
                    Require(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0);
                    Require(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(secret)) < 0);
                }
            }

            reopened.Delete(account.Id);
            var afterDelete = new Store(database, protector);
            Require(afterDelete.Accounts().Count == 0);
            Require(afterDelete.GetSecret(account.Id.ToString()) == null);
            var deletedCache = afterDelete.Cache(account.Id);
            Require(deletedCache.MatchesUpdatedAt == null && deletedCache.Ranks.Count == 0 && deletedCache.Matches.Count == 0 && deletedCache.Opponents.Count == 0 && deletedCache.Forecasts.Count == 0);
            Require(afterDelete.GetSecret("riot-api-key") == apiKey);
        }
    }

    private static void TestWindows(string directory, string? artifactDirectory)
    {
        var application = System.Windows.Application.Current;
        var shutdownMode = application.ShutdownMode;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var store = new Store(Path.Combine(directory, "ui.db"), new WindowsProtector());
            Construct(() => new MainWindow(store, directory), "self-test-empty.png");
            Construct(() => new AccountDialog(store, null), "self-test-account-add.png");
            Construct(() => new SettingsDialog(store, null, directory), "self-test-settings.png");
            var account = new RiotAccount(Guid.NewGuid(), "表示検証", [new("lol", "Dummy", "JP1", "JP1", "dummy-puuid")]);
            store.Save(account, new Credentials("dummy-ui-user", "dummy-ui-password"));
            store.SetSecret("riot-api-key", "dummy-ui-key");
            store.Write("cache", account.Id.ToString(), new AccountCache
            {
                Ranks = [new(DateTimeOffset.UtcNow, [new(Queues.Solo, "GOLD", "IV", 25, 3, 2)])],
                Matches = [new("dummy-match", 420, DateTimeOffset.UtcNow, 1800, false,
                    [new("dummy-puuid", 100, "Ahri", "MIDDLE", 5, 2, 8, 180, 20, true)])]
            });
            Construct(() => new MainWindow(store, directory), "self-test-sample.png");
            Construct(() => new AccountDialog(store, account));
            Construct(() => new SettingsDialog(store, null, directory));
        }
        finally { application.ShutdownMode = shutdownMode; }

        void Construct(Func<Window> create, string? screenshot = null)
        {
            var window = create();
            try
            {
                Require(window.Content != null);
                if (artifactDirectory != null && screenshot != null)
                {
                    Directory.CreateDirectory(artifactDirectory);
                    Render(window, Path.Combine(artifactDirectory, screenshot));
                }
            }
            finally { window.Close(); }
        }
    }

    private static void TestNormalWindows(string directory, string? artifactDirectory)
    {
        var application = System.Windows.Application.Current;
        var shutdownMode = application.ShutdownMode;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow? window = null;
        try
        {
            var store = new Store(Path.Combine(directory, "normal-ui.db"), new WindowsProtector());
            const string puuid = "normal-ui-dummy-puuid";
            var now = DateTimeOffset.UtcNow;
            var account = new RiotAccount(Guid.NewGuid(), "ノーマル表示検証", [new("lol", "Dummy", "JP1", "JP1", puuid)]);
            store.Save(account, new Credentials("normal-ui-dummy-user", "normal-ui-dummy-password"));
            var normal = Queues.Definitions.Single(queue => queue.Key == Queues.Normal);
            Require(!normal.IsRanked && normal.QueueIds.Count() == 4);
            var normalMatches = normal.QueueIds.Select((queueId, index) => new MatchRecord(
                "dummy-normal-" + queueId, queueId, now.AddMinutes(-index * 40), 1800, false,
                [new(puuid, 100, index % 2 == 0 ? "Ahri" : "Lux", index % 2 == 0 ? "MIDDLE" : "SUPPORT", 5, 2, 8, 180, 20, index % 2 == 0)])).ToList();
            store.Write("cache", account.Id.ToString(), new AccountCache
            {
                Ranks = [new(now, [new(Queues.Solo, "GOLD", "IV", 25, 3, 2), new(Queues.Flex, "SILVER", "II", 60, 2, 4)])],
                Forecasts = [new(Queues.Solo, now, 10, 40, 50, "GOLD IV", "GOLD II", "PLATINUM IV", "ダミーのランク予測")],
                QueueUpdatedAt = new() { [Queues.Normal] = now, [Queues.Solo] = now, [Queues.Flex] = now },
                Matches = [.. normalMatches,
                    new("dummy-ranked-solo", 420, now.AddMinutes(-10), 1800, false, [new(puuid, 100, "Caitlyn", "BOTTOM", 2, 5, 3, 150, 10, false)]),
                    new("dummy-ranked-flex", 440, now.AddMinutes(-20), 1800, false, [new(puuid, 100, "Leona", "SUPPORT", 2, 5, 3, 150, 10, false)]),
                    new("dummy-aram", 450, now.AddMinutes(-30), 1800, false, [new(puuid, 100, "Braum", "", 2, 5, 3, 150, 10, false)])]
            });
            window = new MainWindow(store, directory);
            var queuePicker = Control<ComboBox>("QueuePicker");
            var tabs = Control<TabControl>("DetailTabs");
            var overview = Control<TabItem>("OverviewTab");
            var history = Control<TabItem>("HistoryTab");
            var matches = Control<DataGrid>("MatchesGrid");

            Require(queuePicker.Items.Count == 3);
            Require(Control<TextBlock>("RankTitle").Text.Contains("GOLD IV", StringComparison.Ordinal));
            Require(Control<TextBlock>("ForecastRange").Text.Contains("PLATINUM IV", StringComparison.Ordinal));
            tabs.SelectedItem = history;
            queuePicker.SelectedItem = normal;
            VerifyNormal();
            Require(tabs.SelectedItem == overview);

            queuePicker.SelectedItem = Queues.Definitions.Single(queue => queue.Key == Queues.Solo);
            Require(Control<FrameworkElement>("RankedSummaryPanel").Visibility == Visibility.Visible);
            Require(Control<FrameworkElement>("ForecastPanel").Visibility == Visibility.Visible);
            Require(history.Visibility == Visibility.Visible);
            Require(Control<TextBlock>("NormalModeText").Visibility == Visibility.Collapsed);
            Require(Control<TextBlock>("RankTitle").Text.Contains("GOLD IV", StringComparison.Ordinal));
            Require(matches.Items.Count == 1 && Cell(matches.Items[0], "Champion") == "Caitlyn");
            Require(Control<ListBox>("HistoryList").Items.Count == 1);

            queuePicker.SelectedItem = Queues.Definitions.Single(queue => queue.Key == Queues.Flex);
            Require(Control<TextBlock>("RankTitle").Text.Contains("SILVER II", StringComparison.Ordinal));
            Require(matches.Items.Count == 1 && Cell(matches.Items[0], "Champion") == "Leona");
            tabs.SelectedItem = history;
            queuePicker.SelectedItem = normal;
            VerifyNormal();
            Require(tabs.SelectedItem == overview);
            if (artifactDirectory != null)
            {
                Directory.CreateDirectory(artifactDirectory);
                Render(window, Path.Combine(artifactDirectory, "self-test-normal.png"));
                tabs.SelectedItem = Control<TabItem>("MatchesTab");
                Render(window, Path.Combine(artifactDirectory, "self-test-normal-matches.png"));
            }

            T Control<T>(string name) where T : FrameworkElement => window.FindName(name) as T
                ?? throw new InvalidOperationException("Missing self-test control: " + name);

            void VerifyNormal()
            {
                Require(Control<FrameworkElement>("RankedSummaryPanel").Visibility == Visibility.Collapsed);
                Require(Control<FrameworkElement>("ForecastPanel").Visibility == Visibility.Collapsed);
                Require(history.Visibility == Visibility.Collapsed);
                Require(Control<TextBlock>("NormalModeText").Visibility == Visibility.Visible);
                foreach (var name in new[] { "RankTitle", "RankRecord", "RankTime", "ForecastRange", "ForecastDetails" })
                    Require(string.IsNullOrEmpty(Control<TextBlock>(name).Text));
                Require(Control<ListBox>("HistoryList").Items.Count == 0);
                Require(matches.Items.Count == 4);
                var expectedModes = normalMatches.Select(match => Queues.MatchName(match.QueueId)).ToHashSet(StringComparer.Ordinal);
                Require(matches.Items.Cast<object>().Select(row => Cell(row, "Mode")).ToHashSet(StringComparer.Ordinal).SetEquals(expectedModes));
                Require(matches.Items.Cast<object>().Select(row => Cell(row, "Champion")).ToHashSet(StringComparer.Ordinal).SetEquals(["Ahri", "Lux"]));
                Require(matches.Columns.OfType<DataGridTextColumn>().Any(column => column.Binding is System.Windows.Data.Binding { Path.Path: "Mode" }));
                Require(Control<TextBlock>("PerformanceText").Text.StartsWith("4戦  2勝 2敗", StringComparison.Ordinal));
                Require(!Control<TextBlock>("UpdatedText").Text.Contains("未取得", StringComparison.Ordinal));
                var champions = Control<ItemsControl>("ChampionStats").Items.Cast<Performance>().ToList();
                Require(champions.Count == 2 && champions.All(stat => stat.Games == 2));
                Require(champions.Single(stat => stat.Name == "Ahri").Wins == 2);
                Require(champions.Single(stat => stat.Name == "Lux").Wins == 0);
                var roles = Control<ItemsControl>("RoleStats").Items.Cast<Performance>().ToList();
                Require(roles.Count == 2 && roles.All(stat => stat.Games == 2));
                Require(roles.Single(stat => stat.Name == "MIDDLE").Wins == 2);
                Require(roles.Single(stat => stat.Name == "SUPPORT").Wins == 0);
            }
        }
        finally
        {
            window?.Close();
            application.ShutdownMode = shutdownMode;
        }

        static string Cell(object row, string property) => row.GetType().GetProperty(property)?.GetValue(row) as string
            ?? throw new InvalidOperationException("Missing self-test row field: " + property);
    }

    private static void Render(Window window, string file)
    {
        var width = window is MainWindow ? 1220 : (int)window.Width;
        var maximumHeight = window is MainWindow ? 850 : (int)Math.Min(850, window.MaxHeight);
        var content = (FrameworkElement)window.Content;
        // Render an isolated visual tree; no native window, screen capture, or focus changes.
        window.Content = null;
        var surface = new Border { Background = window.Background, Child = content, Width = width, FlowDirection = window.FlowDirection, Language = window.Language };
        try
        {
            System.Windows.Documents.TextElement.SetFontFamily(surface, window.FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(surface, window.FontSize);
            System.Windows.Documents.TextElement.SetForeground(surface, window.Foreground);
            System.Windows.Documents.TextElement.SetFontWeight(surface, window.FontWeight);
            System.Windows.Documents.TextElement.SetFontStyle(surface, window.FontStyle);
            System.Windows.Documents.TextElement.SetFontStretch(surface, window.FontStretch);
            if (window is MainWindow) surface.Height = maximumHeight;
            surface.ApplyTemplate();
            surface.Measure(new Size(width, maximumHeight));
            var height = Math.Max(1, Math.Min(maximumHeight, (int)Math.Ceiling(surface.DesiredSize.Height)));
            surface.Height = height;
            surface.Measure(new Size(width, height));
            surface.Arrange(new Rect(0, 0, width, height));
            surface.UpdateLayout();
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            image.Render(surface);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var output = File.Create(file);
            encoder.Save(output);
        }
        finally
        {
            surface.Child = null;
            window.Content = content;
        }
    }

    private static void TestNativeHelpers()
    {
        Require(Win.InputSize == (IntPtr.Size == 8 ? 40 : 28));
        var blank = MakeImage(false);
        var changed = MakeImage(true);
        var onePixel = MakeImage(false, true);
        Require(NativeLogin.TemplatesMatch(blank, blank, 500, 300, 150, 90, 150, 190));
        Require(!NativeLogin.TemplatesMatch(blank, changed, 500, 300, 150, 90, 150, 190));
        Require(!NativeLogin.TemplatesMatch(blank, onePixel, 500, 300, 150, 90, 150, 190));
        Require(!NativeLogin.TemplatesMatch(blank, blank, 501, 300, 150, 90, 150, 190));

        static byte[] MakeImage(bool changed, bool onePixel = false)
        {
            using var bitmap = new System.Drawing.Bitmap(500, 300);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.White);
                if (changed) graphics.FillRectangle(System.Drawing.Brushes.Black, 40, 80, 180, 15);
            }
            if (onePixel) bitmap.SetPixel(127, 90, System.Drawing.Color.Black);
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }
    }

    private static void TestCalibrationGeometry()
    {
        var calibration = new Calibration(800, 600, 96, "dummy-version", 100, 100, 100, 200, 150, 300, [1], 2,
            new(40, 80, 250, 40), new(40, 180, 250, 40), [1], new(120, 280, 60, 40));
        Require(NativeLogin.CalibrationGeometryMatches(calibration, 800, 600, 96, "dummy-version"));
        Require(!NativeLogin.CalibrationGeometryMatches(calibration, 801, 600, 96, "dummy-version"));
        Require(!NativeLogin.CalibrationGeometryMatches(calibration, 800, 601, 96, "dummy-version"));
        Require(!NativeLogin.CalibrationGeometryMatches(calibration, 800, 600, 144, "dummy-version"));
        Require(!NativeLogin.CalibrationGeometryMatches(calibration, 800, 600, 96, "updated-version"));
        foreach (var invalid in new[]
        {
            calibration with { SchemaVersion = 0 }, calibration with { UserBounds = null },
            calibration with { PasswordBounds = new(799, 180, 250, 40) }, calibration with { UserX = 799 },
            calibration with { PasswordY = 599 }, calibration with { SubmitX = 800 },
            calibration with { PasswordTemplate = null }
        }) Require(!NativeLogin.CalibrationGeometryMatches(invalid, 800, 600, 96, "dummy-version"));
    }
}
