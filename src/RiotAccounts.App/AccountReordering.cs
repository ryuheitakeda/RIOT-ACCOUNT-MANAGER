using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RiotAccounts.App;

public partial class MainWindow
{
    private const string AccountDragFormat = "RiotAccounts.AccountOrder";
    private static readonly DependencyPropertyKey IsAccountReorderingEnabledPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsAccountReorderingEnabled), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public static readonly DependencyProperty IsAccountReorderingEnabledProperty = IsAccountReorderingEnabledPropertyKey.DependencyProperty;
    public bool IsAccountReorderingEnabled => (bool)GetValue(IsAccountReorderingEnabledProperty);
    private Button? reorderHandle;
    private Guid? reorderAccountId;
    private Point reorderStart;
    private bool accountDragActive;
    private DispatcherTimer? reorderScrollTimer;
    private Point? reorderPointer;
    private sealed record AccountDragData(MainWindow Source, Guid AccountId);
    internal readonly record struct AccountDropTarget(Guid AccountId, bool After, double IndicatorY);

    private void UpdateAccountReorderingAvailability()
    {
        var available = operation == null && string.IsNullOrWhiteSpace(Search.Text) && AccountsList.Items.Count > 1;
        SetValue(IsAccountReorderingEnabledPropertyKey, available);
        // Store the value on the list so handle bindings also work in detached visual self-tests.
        AccountsList.Tag = available;
        AccountReorderHint.Text = operation != null ? "処理中は並べ替えできません。"
            : !string.IsNullOrWhiteSpace(Search.Text) ? "検索を解除すると並べ替えできます。"
            : AccountsList.Items.Count < 2 ? "2件以上で並べ替えできます。"
            : "三本線をドラッグして並べ替え\nハンドルで Alt＋↑／↓ も使えます。";
        if (!available) ClearAccountDropFeedback();
    }

    internal bool MoveAccountInList(Guid id, Guid target, bool after)
    {
        if (operation != null || !string.IsNullOrWhiteSpace(Search.Text) || id == target) return false;
        var selectedId = Selected?.Id;
        try
        {
            store.MoveAccount(id, target, after);
            Reload(selectedId);
            StatusText.Text = "アカウントの順番を保存しました。";
            return true;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or ArgumentException)
        {
            StatusText.Text = "順番を保存できませんでした。保存先とアカウント一覧を確認してください。";
            return false;
        }
    }

    internal bool MoveAccountByKeyboard(Guid id, int direction)
    {
        if (!IsAccountReorderingEnabled || direction is not (-1 or 1)) return false;
        var items = AccountsList.Items.Cast<AccountItem>().ToList();
        var index = items.FindIndex(item => item.Account.Id == id);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= items.Count) return false;
        return MoveAccountInList(id, items[target].Account.Id, direction > 0);
    }

    internal static bool IsAccountDragThresholdReached(Point start, Point current) =>
        Math.Abs(current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;

    internal static int AccountReorderKeyDirection(Key key, ModifierKeys modifiers) => modifiers == ModifierKeys.Alt
        ? key switch { Key.Up => -1, Key.Down => 1, _ => 0 } : 0;

    private void ReorderHandle_Down(object sender, MouseButtonEventArgs e)
    {
        if (!IsAccountReorderingEnabled || sender is not Button { DataContext: AccountItem item } handle) return;
        e.Handled = true; // The handle must not perform the row's usual selection action.
        reorderHandle = handle;
        reorderAccountId = item.Account.Id;
        reorderStart = e.GetPosition(AccountsList);
        handle.Focus();
        handle.CaptureMouse();
    }

    private void ReorderHandle_Move(object sender, MouseEventArgs e)
    {
        if (accountDragActive || reorderHandle == null || reorderAccountId is not { } id) return;
        if (e.LeftButton != MouseButtonState.Pressed || !IsAccountReorderingEnabled) { EndAccountPointerGesture(); return; }
        if (!IsAccountDragThresholdReached(reorderStart, e.GetPosition(AccountsList))) return;
        var handle = reorderHandle;
        accountDragActive = true;
        handle.ReleaseMouseCapture();
        var data = new DataObject();
        data.SetData(AccountDragFormat, new AccountDragData(this, id));
        try { DragDrop.DoDragDrop(handle, data, DragDropEffects.Move); }
        finally
        {
            accountDragActive = false;
            EndAccountPointerGesture();
            ClearAccountDropFeedback();
            FocusAccountHandle(id);
        }
        e.Handled = true;
    }

    private void ReorderHandle_Up(object sender, MouseButtonEventArgs e)
    {
        if (reorderHandle == null) return;
        EndAccountPointerGesture();
        e.Handled = true;
    }

    private void ReorderHandle_LostCapture(object sender, MouseEventArgs e)
    {
        if (!accountDragActive) EndAccountPointerGesture();
    }

    private void EndAccountPointerGesture()
    {
        var handle = reorderHandle;
        reorderHandle = null;
        reorderAccountId = null;
        if (handle?.IsMouseCaptured == true) handle.ReleaseMouseCapture();
    }

    private void ReorderHandle_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !accountDragActive) { EndAccountPointerGesture(); return; }
        var direction = AccountReorderKeyDirection(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
        if (direction == 0 || sender is not Button { DataContext: AccountItem item }) return;
        e.Handled = true;
        if (MoveAccountByKeyboard(item.Account.Id, direction)) FocusAccountHandle(item.Account.Id);
    }

    private void ReorderHandle_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed || !IsAccountReorderingEnabled)
        {
            e.Action = DragAction.Cancel;
            e.Handled = true;
            ClearAccountDropFeedback();
        }
    }

    private bool TryReadAccountDrag(DragEventArgs e, out Guid id)
    {
        id = default;
        if (!accountDragActive || !IsAccountReorderingEnabled || !e.Data.GetDataPresent(AccountDragFormat)) return false;
        if (e.Data.GetData(AccountDragFormat) is not AccountDragData data || !ReferenceEquals(data.Source, this)) return false;
        id = data.AccountId;
        return true;
    }

    private void Accounts_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        var point = e.GetPosition(AccountsList);
        if (!TryReadAccountDrag(e, out _) || !TryGetAccountDropTarget(point, out var target))
        {
            ClearAccountDropFeedback();
            return;
        }
        e.Effects = DragDropEffects.Move;
        reorderPointer = point;
        ShowAccountDropFeedback(target);
        StartAccountAutoScroll();
    }

    private void Accounts_DragLeave(object sender, DragEventArgs e)
    {
        if (!IsInsideAccountList(e.GetPosition(AccountsList))) ClearAccountDropFeedback();
    }

    private void Accounts_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (TryReadAccountDrag(e, out var id) && TryGetAccountDropTarget(e.GetPosition(AccountsList), out var target)
            && MoveAccountInList(id, target.AccountId, target.After)) e.Effects = DragDropEffects.Move;
        ClearAccountDropFeedback();
    }

    private bool IsInsideAccountList(Point point) =>
        point.X >= 0 && point.X < AccountsList.ActualWidth && point.Y >= 0 && point.Y < AccountsList.ActualHeight;

    internal bool TryGetAccountDropTarget(Point point, out AccountDropTarget target)
    {
        target = default;
        if (!IsInsideAccountList(point)) return false;
        var found = false;
        for (var index = 0; index < AccountsList.Items.Count; index++)
        {
            if (AccountsList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem row || row.ActualHeight <= 0) continue;
            var top = row.TranslatePoint(new Point(), AccountsList).Y;
            var bottom = top + row.ActualHeight;
            if (bottom < 0 || top > AccountsList.ActualHeight) continue;
            var id = ((AccountItem)AccountsList.Items[index]).Account.Id;
            if (point.Y < top + row.ActualHeight / 2)
            {
                target = new(id, false, Math.Clamp(top, 0, Math.Max(0, AccountsList.ActualHeight - 3)));
                return true;
            }
            target = new(id, true, Math.Clamp(bottom, 0, Math.Max(0, AccountsList.ActualHeight - 3)));
            if (point.Y <= bottom) return true;
            found = true;
        }
        return found;
    }

    private void ShowAccountDropFeedback(AccountDropTarget target)
    {
        Canvas.SetTop(AccountDropIndicator, target.IndicatorY);
        AccountDropIndicator.Visibility = Visibility.Visible;
    }

    private void ClearAccountDropFeedback()
    {
        AccountDropIndicator.Visibility = Visibility.Collapsed;
        reorderPointer = null;
        reorderScrollTimer?.Stop();
    }

    private void StartAccountAutoScroll()
    {
        if (reorderScrollTimer == null)
        {
            reorderScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            reorderScrollTimer.Tick += (_, _) => ScrollAccountsAtPointer();
        }
        reorderScrollTimer.Start();
    }

    private void ScrollAccountsAtPointer()
    {
        if (!accountDragActive || !IsAccountReorderingEnabled || reorderPointer is not { } point || !IsInsideAccountList(point))
        { ClearAccountDropFeedback(); return; }
        if (FindAccountVisual<ScrollViewer>(AccountsList) is not { } scroll) return;
        const double edge = 32;
        if (point.Y < edge) scroll.LineUp();
        else if (point.Y > AccountsList.ActualHeight - edge) scroll.LineDown();
        else return;
        AccountsList.UpdateLayout();
        if (TryGetAccountDropTarget(point, out var target)) ShowAccountDropFeedback(target);
    }

    private void FocusAccountHandle(Guid id)
    {
        var item = AccountsList.Items.Cast<AccountItem>().FirstOrDefault(item => item.Account.Id == id);
        if (item == null) return;
        AccountsList.ScrollIntoView(item);
        AccountsList.UpdateLayout();
        if (AccountsList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem row)
            FindAccountVisual<Button>(row)?.Focus();
    }

    private static T? FindAccountVisual<T>(DependencyObject element) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            var child = VisualTreeHelper.GetChild(element, index);
            if (child is T match) return match;
            if (FindAccountVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
