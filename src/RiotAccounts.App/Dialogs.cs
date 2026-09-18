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

public sealed class SettingsDialog : Window
{
    public bool CalibrateRequested { get; private set; }

    public SettingsDialog(Store store, string? clientPath, string folder)
    {
        var form = DialogLayout.Form(this, "設定", 580);
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
        var path = new TextBox { Text = clientPath ?? "" };
        DialogLayout.Field(form, "Riotクライアント（RiotClientServices.exe）", path);
        var browse = new Button { Content = "実行ファイルを選択", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
        browse.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Riot Client|RiotClientServices.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) == true) path.Text = picker.FileName;
        };
        form.Children.Add(browse);
        form.Children.Add(DialogLayout.Hint("位置登録を使う場合は、先にRiotクライアントからログアウトし、ID・パスワードが空のログイン画面にしてください。登録時は各入力欄とログインボタンにマウスを置き、F8で確定します。Escで中止できます。"));
        var emptyConfirmed = new CheckBox { Content = "RiotのID・パスワード欄がどちらも空であることを確認しました", Margin = new Thickness(0, 0, 0, 12) };
        form.Children.Add(emptyConfirmed);
        var calibrate = new Button { Content = "保存してログイン位置を登録", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
        form.Children.Add(calibrate);
        form.Children.Add(DialogLayout.Hint($"保存先：{folder}\n暗号化データは別のWindowsユーザーでは復号できません。"));
        var error = DialogLayout.Hint(""); error.Foreground = System.Windows.Media.Brushes.Firebrick; form.Children.Add(error);
        bool Save()
        {
            var selectedPath = path.Text.Trim().Trim('"');
            if (selectedPath.Length > 0 && (!File.Exists(selectedPath) || !string.Equals(Path.GetFileName(selectedPath), "RiotClientServices.exe", StringComparison.OrdinalIgnoreCase)))
            { error.Text = "実在するRiotClientServices.exeを選択してください。"; return false; }
            try
            {
                if (clearKey.IsChecked == true) store.SetSecret("riot-api-key", "");
                else if (!string.IsNullOrWhiteSpace(key.Password)) store.SetSecret("riot-api-key", key.Password.Trim());
                store.Write("setting", "clientPath", selectedPath);
                store.Write("setting", "statsSource", source.SelectedValue as string == "opgg" ? "opgg" : "riot");
                key.Clear(); return true;
            }
            catch (Exception ex) when (ex is CryptographicException or SqliteException)
            { error.Text = "設定を保存できませんでした。保存先のアクセス権を確認してください。"; return false; }
        }
        calibrate.Click += (_, _) =>
        {
            if (emptyConfirmed.IsChecked != true) { error.Text = "空のログイン画面を用意して、確認欄をチェックしてください。"; return; }
            if (Save()) { CalibrateRequested = true; DialogResult = true; }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "キャンセル", IsCancel = true };
        cancel.Click += (_, _) => DialogResult = false;
        var save = new Button { Content = "保存", IsDefault = true, Style = (Style)FindResource("Primary") };
        save.Click += (_, _) => { if (Save()) DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(save); form.Children.Add(buttons);
    }
}
