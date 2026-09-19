using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace RiotAccounts.App;

public sealed class LoginSetupDialog : Window
{
    private readonly Store store;
    private readonly NativeLogin login;
    private readonly StackPanel form;
    private readonly TextBlock heading = DialogLayout.Hint("");
    private readonly TextBlock message = DialogLayout.Hint("");
    private readonly StackPanel actions = new() { Margin = new Thickness(0, 8, 0, 0) };
    private CancellationTokenSource? operation;
    private bool closeRequested;
    public bool Completed { get; private set; }

    public LoginSetupDialog(Store store, NativeLogin login, string? clientPath)
    {
        this.store = store;
        this.login = login;
        form = DialogLayout.Form(this, "自動入力の設定", 580);
        heading.FontSize = 22;
        heading.FontWeight = FontWeights.SemiBold;
        form.Children.Add(heading);
        form.Children.Add(message);
        form.Children.Add(actions);
        Closing += (_, e) =>
        {
            if (operation == null) return;
            closeRequested = true;
            operation.Cancel();
            e.Cancel = true;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            if (operation != null) operation.Cancel(); else Close();
            e.Handled = true;
        };
        ShowClient(clientPath);
    }

    private void Page(string title, string description)
    {
        heading.Text = title;
        message.Text = description;
        actions.Children.Clear();
    }

    private void Button(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        if (primary) button.Style = (Style)FindResource("Primary");
        button.Click += (_, _) => action();
        actions.Children.Add(button);
    }

    private void ShowClient(string? clientPath)
    {
        Page("1. クライアントの確認", clientPath == null
            ? "Riotクライアントが見つかりません。RiotClientServices.exeを選択してください。"
            : "Riotクライアントが見つかりました。このまま次へ進めます。");
        var path = new TextBox { Text = clientPath ?? "" };
        DialogLayout.Field(actions, "Riotクライアントの場所", path);
        Button("ファイルを選択・変更", () =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Riot Client|RiotClientServices.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) == true) path.Text = picker.FileName;
        });
        actions.Children.Add(DialogLayout.Hint("「次へ」でクライアントの場所を保存します。"));
        Button("次へ：ログイン画面を準備", () =>
        {
            var selected = path.Text.Trim().Trim('"');
            if (!File.Exists(selected) || !string.Equals(Path.GetFileName(selected), "RiotClientServices.exe", StringComparison.OrdinalIgnoreCase))
            {
                message.Text = "ファイルが見つかりません。「ファイルを選択・変更」でRiotClientServices.exeを選択してください。";
                return;
            }
            try { store.Write("setting", "clientPath", selected); ShowPreparation(); }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            { message.Text = "保存できませんでした。保存先の空き容量・アクセス権を確認して再試行してください。"; }
        }, true);
        Button("閉じる", Close);
    }

    private void ShowPreparation(string? notice = null)
    {
        Page("2. ログイン画面の準備", (notice == null ? "" : notice + "\n\n") +
            "① Riotクライアントを開きます。\n② ログイン中ならログアウトします。\n③ ID・パスワードを両方空にして、この案内へ戻ります。");
        Button("Riotクライアントを開く", () => Start(async ct =>
        {
            await login.OpenForSetupAsync(ct);
            // Leave Riot in front so the user can prepare the form.
        }, false));
        var empty = new CheckBox { Content = new TextBlock { Text = "ID・パスワード欄を両方空にしました" }, Margin = new Thickness(0, 8, 0, 16) };
        actions.Children.Add(empty);
        Button("入力欄を確認する", () =>
        {
            if (empty.IsChecked != true) { message.Text = "Riotのログイン画面でID・パスワード欄を空にして、確認欄にチェックしてください。"; return; }
            Start(async ct =>
            {
                if (await login.CheckFieldsAsync(ct)) ShowComplete(false);
                else ShowRegistration();
            });
        }, true);
        Button("戻る：クライアントの場所", () => ShowClient(login.ClientPath()));
        Button("閉じる", Close);
    }

    private void ShowRegistration(string? notice = null)
    {
        Page("3. 入力位置の登録", (notice ?? "入力欄を自動で見つけられませんでした。位置を指定して確認できます。") +
            "\n\nID欄 → パスワード欄 → ログインボタンの順に、マウスを置いてF8を押します。クリックは不要です。Escで中止できます。\n\n開始前にID・パスワード欄が両方空であることを確認してください。");
        var empty = new CheckBox { Content = new TextBlock { Text = "ID・パスワード欄が両方空であることを確認しました" }, Margin = new Thickness(0, 0, 0, 16) };
        actions.Children.Add(empty);
        Button("位置登録を開始（1/3 ID欄から）", () =>
        {
            if (empty.IsChecked != true) { message.Text = "両方の入力欄を空にして、確認欄にチェックしてください。"; return; }
            Start(async ct =>
            {
                var label = new TextBlock { Text = "Riot画面を確認中…\nEscで中止できます。", Width = 280, Margin = new Thickness(16) };
                var prompt = new Window
                {
                    Title = "入力位置の登録", Content = label, SizeToContent = SizeToContent.WidthAndHeight,
                    Topmost = true, ShowActivated = false, WindowStyle = WindowStyle.ToolWindow,
                    Left = SystemParameters.WorkArea.Left + 10, Top = SystemParameters.WorkArea.Top + 10,
                    IsHitTestVisible = false
                };
                var active = true;
                var progress = new Progress<CalibrationProgress>(step =>
                {
                    if (active) label.Text = $"{step.Step}/3  {step.Field}\n\n{step.Field}にマウスを置いてF8を押してください。\nEsc：中止";
                });
                // Preserve the modal session while Riot owns foreground focus.
                Opacity = 0;
                try { prompt.Show(); await login.CalibrateAsync(progress, ct); }
                finally { active = false; prompt.Close(); Opacity = 1; }
                ShowComplete(true);
            }, registration: true);
        }, true);
        Button("戻って入力欄を再確認", () => ShowPreparation());
        Button("個別コピーを使う", ShowCopyHelp);
    }

    private void ShowCopyHelp()
    {
        Page("個別コピーで入力", "アカウント画面の「IDをコピー」「パスワードをコピー」を使い、Riotの対応する入力欄にCtrl+Vで貼り付けてください。\n\nパスワードのコピーは30秒後に消去されます。");
        Button("アカウント画面へ戻る", Close, true);
    }

    private void ShowComplete(bool registered)
    {
        Completed = true;
        Page("設定の確認が完了しました", registered
            ? "ログイン位置を登録しました。画面サイズ・表示倍率・クライアントの更新で配置が変わった場合は、この案内から再登録してください。"
            : "入力欄を確認できました。位置登録は不要です。");
        actions.Children.Add(DialogLayout.Hint("アカウントを選んで「Riotにログイン」を押すと利用できます。今回の確認ではID・パスワードの入力やログイン送信はしていません。"));
        Button("アカウント画面へ戻る", Close, true);
    }

    private async void Start(Func<CancellationToken, Task> action, bool activateAfter = true, bool registration = false)
    {
        if (operation != null) return;
        operation = new CancellationTokenSource();
        actions.IsEnabled = false;
        try { await action(operation.Token); }
        catch (OperationCanceledException)
        {
            if (registration) ShowRegistration("位置登録を中止しました。以前の登録は変更していません。最初からやり直せます。");
            else ShowPreparation("確認を中止しました。準備して再試行できます。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception
            or ExternalException or CryptographicException or SqliteException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            var reason = ex is InvalidOperationException ? ex.Message : "操作できませんでした。クライアントの起動状態・Windows権限・保存先を確認してください。";
            if (registration) ShowRegistration(reason + "\n登録は更新していません。最初からやり直せます。");
            else
            {
                ShowPreparation(reason);
                Button("個別コピーを使う", ShowCopyHelp);
            }
        }
        finally
        {
            operation.Dispose();
            operation = null;
            actions.IsEnabled = true;
            if (closeRequested) Close();
            else if (activateAfter) Activate();
        }
    }
}
