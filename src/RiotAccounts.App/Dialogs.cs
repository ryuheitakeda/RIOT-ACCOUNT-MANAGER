using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows.Automation;
using Microsoft.Data.Sqlite;

namespace RiotAccounts.App;

internal static class DialogLayout
{
    public static StackPanel Form(Window window, string title, double width = 480)
    {
        window.Title = title;
        window.Style = (Style)System.Windows.Application.Current.FindResource(typeof(Window));
        window.Width = width;
        window.SizeToContent = SizeToContent.Height;
        window.MaxHeight = SystemParameters.WorkArea.Height - 60;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.ResizeMode = ResizeMode.NoResize;
        var form = new StackPanel { Margin = new Thickness(24) };
        window.Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return form;
    }

    public static void Field(Panel panel, string name, Control input)
    {
        AutomationProperties.SetName(input, name);
        panel.Children.Add(new Label { Content = name, Target = input, Padding = new Thickness(0) });
        panel.Children.Add(input);
    }

    public static TextBlock Hint(string text) => new() { Text = text, Margin = new Thickness(0, 4, 0, 16) };
}

public sealed class AccountDialog : Window
{
    public Guid SavedId { get; private set; }

    public AccountDialog(Store store, RiotAccount? account)
    {
        var form = DialogLayout.Form(this, account == null ? "アカウントを追加" : "アカウントを編集");
        var label = new TextBox { Text = account?.Label ?? "" };
        var username = new TextBox();
        var password = new PasswordBox();
        var gameName = new TextBox { Text = account?.Lol.GameName ?? "" };
        var tag = new TextBox { Text = account?.Lol.TagLine ?? "" };
        var platform = new ComboBox { ItemsSource = Regions.Routing.Keys.ToList(), SelectedItem = account?.Lol.Platform ?? "JP1" };
        if (account != null) username.Text = store.Credentials(account.Id).Username;
        DialogLayout.Field(form, "管理名", label);
        DialogLayout.Field(form, "ログインID（Riot IDとは別）", username);
        DialogLayout.Field(form, account == null ? "パスワード" : "パスワード（空欄なら変更しません）", password);
        DialogLayout.Field(form, "Riot ID：ゲーム内の名前", gameName);
        DialogLayout.Field(form, "Riot ID：タグ（#を除く）", tag);
        DialogLayout.Field(form, "サーバー", platform);
        if (account != null) form.Children.Add(DialogLayout.Hint("Riot ID・サーバーを変更すると、このアカウントの保存済みランク・戦績を消去し、次回更新で取得し直します。"));
        form.Children.Add(DialogLayout.Hint("ログイン情報は、このWindowsユーザーだけが復号できる形式で保存します。"));
        var error = DialogLayout.Hint("");
        error.Foreground = System.Windows.Media.Brushes.Firebrick;
        form.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "キャンセル", IsCancel = true };
        var save = new Button { Content = "保存", IsDefault = true, Style = (Style)FindResource("Primary") };
        cancel.Click += (_, _) => DialogResult = false;
        save.Click += (_, _) =>
        {
            try
            {
                var actualPassword = password.Password;
                if (account != null && actualPassword.Length == 0) actualPassword = store.Credentials(account.Id).Password;
                var server = platform.SelectedItem as string ?? "";
                var profile = new GameProfile("lol", gameName.Text.Trim(), tag.Text.Trim().TrimStart('#'), server);
                if (account != null && account.Lol.GameName == profile.GameName && account.Lol.TagLine == profile.TagLine && account.Lol.Platform == server)
                    profile = profile with { Puuid = account.Lol.Puuid };
                var profiles = account?.Profiles.Where(p => p.Game != "lol").ToList() ?? [];
                profiles.Add(profile);
                SavedId = account?.Id ?? Guid.NewGuid();
                store.Save(new RiotAccount(SavedId, label.Text.Trim(), profiles), new Credentials(username.Text.Trim(), actualPassword));
                password.Clear();
                DialogResult = true;
            }
            catch (ArgumentException) { error.Text = "管理名・ログインID・パスワード・Riot ID・サーバーを入力してください。"; }
            catch (Exception ex) when (ex is CryptographicException or SqliteException or InvalidOperationException)
            { error.Text = "保存できませんでした。保存先の空き容量・アクセス権を確認してください。"; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); form.Children.Add(buttons);
        Loaded += (_, _) => label.Focus();
    }
}

public sealed class DiagnosticsDialog : Window
{
    public DiagnosticsDialog(DiagnosticReport report, string logPath)
    {
        var form = DialogLayout.Form(this, "接続の診断", 660);
        form.Children.Add(new TextBlock
        {
            Text = report.Ok ? "すべての段階に成功しました。" : $"「{report.Failed!.Name}」で失敗しました。",
            FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12)
        });
        var box = new TextBox
        {
            Text = report.ToText(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160, MaxHeight = 380,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas, Meiryo")
        };
        AutomationProperties.SetName(box, "診断結果");
        form.Children.Add(box);
        form.Children.Add(DialogLayout.Hint("APIキー・PUUID・応答の内容は含まれません。更新処理の記録：" + logPath));
        var copy = new Button { Content = "結果をコピー", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(report.ToText()); copy.Content = "コピーしました"; }
            catch (System.Runtime.InteropServices.ExternalException) { copy.Content = "コピーできませんでした"; }
        };
        form.Children.Add(copy);
        var close = new Button { Content = "閉じる", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Left, Style = (Style)FindResource("Primary") };
        close.Click += (_, _) => Close();
        form.Children.Add(close);
    }
}

public sealed class SettingsDialog : Window
{
    public bool SetupRequested { get; private set; }

    public SettingsDialog(Store store, string? clientPath, string folder)
    {
        var form = DialogLayout.Form(this, "設定", 580);
        form.Children.Add(new TextBlock { Text = "自動入力", FontSize = 20, FontWeight = FontWeights.SemiBold });
        form.Children.Add(DialogLayout.Hint("準備から入力欄の確認まで順番に案内します。APIキーは不要です。"));
        form.Children.Add(DialogLayout.Hint(clientPath == null ? "Riotクライアント：未検出（案内の中で選択できます）" : "Riotクライアント：検出済み"));
        var setup = new Button { Content = "自動入力を設定する", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 24), Style = (Style)FindResource("Primary") };
        form.Children.Add(setup);
        form.Children.Add(new TextBlock { Text = "ランク・戦績用APIキー", FontSize = 20, FontWeight = FontWeights.SemiBold });
        var key = new PasswordBox();
        var hasKey = !string.IsNullOrEmpty(store.GetSecret("riot-api-key"));
        DialogLayout.Field(form, hasKey ? "Riot APIキー（登録済み／空欄なら変更しません）" : "Riot APIキー", key);
        form.Children.Add(DialogLayout.Hint("開発用APIキーは24時間で失効します。Riot Developer Portalで取得し、ここに貼り付けてください。ログイン補助だけなら不要です。"));
        var portal = new Button { Content = "Riot Developer Portalを開く", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
        portal.Click += (_, _) => Process.Start(new ProcessStartInfo("https://developer.riotgames.com/") { UseShellExecute = true });
        form.Children.Add(portal);
        var clearKey = new CheckBox { Content = "保存済みのAPIキーを削除する", Margin = new Thickness(0, 0, 0, 20) };
        form.Children.Add(clearKey);
        var source = new ComboBox { DisplayMemberPath = "Value", SelectedValuePath = "Key" };
        source.ItemsSource = new Dictionary<string, string> { ["riot"] = "Riot API（公式・APIキーが必要）", ["opgg"] = "OP.GG（非公式・APIキー不要）" };
        source.SelectedValue = store.Read<string>("setting", "statsSource") == "opgg" ? "opgg" : "riot";
        DialogLayout.Field(form, "ランク・戦績の取得元", source);
        form.Children.Add(DialogLayout.Hint("OP.GGは非公式の取得方法です。仕様変更で突然使えなくなることがあり、OP.GG側の更新が遅れると最新の試合が含まれません。参考ランク帯はRiot API利用時のみ算出します。"));
        form.Children.Add(DialogLayout.Hint($"保存先：{folder}\n暗号化データは別のWindowsユーザーでは復号できません。"));
        var error = DialogLayout.Hint(""); error.Foreground = System.Windows.Media.Brushes.Firebrick; form.Children.Add(error);
        var warned = false;
        key.PasswordChanged += (_, _) => warned = false;
        bool Save()
        {
            try
            {
                if (clearKey.IsChecked == true) store.SetSecret("riot-api-key", "");
                else if (!string.IsNullOrWhiteSpace(key.Password))
                {
                    // An odd-looking key is saved only on the second press, so a bad paste is noticed before it is stored.
                    var problems = RiotKeyFormat.Problems(key.Password.Trim());
                    if (problems.Count > 0 && !warned)
                    {
                        warned = true;
                        error.Text = "APIキーの形式が通常と異なります：" + string.Join("／", problems) + "。貼り付けをやり直すか、このまま保存する場合はもう一度「保存」を押してください。";
                        return false;
                    }
                    store.SetSecret("riot-api-key", key.Password.Trim());
                }
                store.Write("setting", "statsSource", source.SelectedValue as string == "opgg" ? "opgg" : "riot");
                key.Clear(); return true;
            }
            catch (Exception ex) when (ex is CryptographicException or SqliteException)
            { error.Text = "設定を保存できませんでした。保存先のアクセス権を確認してください。"; return false; }
        }
        setup.Click += (_, _) =>
        {
            var savedSource = store.Read<string>("setting", "statsSource") == "opgg" ? "opgg" : "riot";
            if (!string.IsNullOrEmpty(key.Password) || clearKey.IsChecked == true || source.SelectedValue as string != savedSource)
            { error.Text = "APIキー・取得元の変更を先に「保存」してから、自動入力の設定を開いてください。"; return; }
            SetupRequested = true; DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "キャンセル", IsCancel = true };
        cancel.Click += (_, _) => DialogResult = false;
        var save = new Button { Content = "保存", IsDefault = true, Style = (Style)FindResource("Primary") };
        save.Click += (_, _) => { if (Save()) DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(save); form.Children.Add(buttons);
    }
}

public sealed class ScoreboardDialog : Window
{
    public ScoreboardDialog(MatchRecord match, string puuid)
    {
        var form = DialogLayout.Form(this, "試合詳細", 640);
        form.Children.Add(new TextBlock
        {
            Text = $"{Queues.MatchName(match.QueueId)}  /  {match.StartedAt.LocalDateTime:yyyy/MM/dd HH:mm}  /  {match.DurationSeconds / 60}分{match.DurationSeconds % 60:D2}秒",
            FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4)
        });
        form.Children.Add(DialogLayout.Hint("他のプレイヤーの名前は保存していないため、チャンピオンと成績のみ表示します。"));
        var selfRow = new Style(typeof(DataGridRow));
        var trigger = new DataTrigger { Binding = new System.Windows.Data.Binding(nameof(ScoreboardRow.IsSelf)), Value = true };
        trigger.Setters.Add(new Setter(BackgroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDD, 0xEA, 0xF4))));
        trigger.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold));
        selfRow.Triggers.Add(trigger);
        foreach (var team in Analytics.Scoreboard(match, puuid))
        {
            form.Children.Add(new TextBlock { Text = team.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6) });
            var grid = new DataGrid
            {
                ItemsSource = team.Players, IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, BorderThickness = new Thickness(0), RowHeight = 32, RowStyle = selfRow,
                Margin = new Thickness(0, 0, 0, 12)
            };
            void Column(string header, string path, DataGridLength width) =>
                grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path), Width = width });
            Column("チャンピオン", nameof(ScoreboardRow.Champion), new DataGridLength(1, DataGridLengthUnitType.Star));
            Column("ロール", nameof(ScoreboardRow.Role), new DataGridLength(90));
            Column("K / D / A", nameof(ScoreboardRow.Kda), new DataGridLength(100));
            Column("CS/分", nameof(ScoreboardRow.CsPerMinute), new DataGridLength(70));
            Column("視界", nameof(ScoreboardRow.VisionScore), new DataGridLength(60));
            AutomationProperties.SetName(grid, team.Title);
            form.Children.Add(grid);
        }
        var close = new Button { Content = "閉じる", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)FindResource("Primary") };
        close.Click += (_, _) => Close();
        form.Children.Add(close);
    }
}
