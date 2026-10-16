using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Something.Storage;

namespace Something.App;

internal sealed record ManagedHistoryRow(ScanHistoryEntry Entry)
{
    public string CompletedText => Entry.CompletedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");
    public string ElapsedText => $"{Entry.Elapsed.TotalSeconds:N1} 秒";
}

public sealed partial class HistoryManagementView : UserControl
{
    private ScanHistoryArchive? archive;
    private Func<Guid[], Task>? refreshViews;
    private CancellationToken token;
    private string? volumeKey;
    private HashSet<Guid> selectedIds = [];
    private Guid[]? confirmed;
    private int loadVersion;
    private bool initializing = true;
    private bool busy;
    internal bool IsDeleting { get; private set; }
    internal event Action<Task>? WorkStarted;
    internal event Action<bool>? DeletingChanged;

    public HistoryManagementView()
    {
        InitializeComponent();
        FromTime.Time = TimeSpan.Zero;
        EndTime.Time = TimeSpan.Zero;
        initializing = false;
        UpdateRangeText();
    }

    internal void Configure(
        ScanHistoryArchive? archive,
        Func<Guid[], Task> refreshViews,
        CancellationToken token
    )
    {
        this.archive = archive;
        this.refreshViews = refreshViews;
        this.token = token;
    }

    internal async Task LoadAsync(string? key)
    {
        if (IsDeleting)
            return;
        int version = ++loadVersion;
        bool changed = volumeKey != key;
        var selected = changed ? [] : selectedIds.ToHashSet();
        volumeKey = key;
        initializing = true;
        if (changed)
        {
            selectedIds.Clear();
            FromDate.Date = EndDate.Date = null;
        }
        HistoryList.ItemsSource = null;
        initializing = false;
        UpdateRangeText();
        SelectionError.Visibility = Visibility.Collapsed;
        confirmed = null;
        ConfirmationPanel.Visibility = Visibility.Collapsed;
        Notice.IsOpen = false;
        SetBusy(true);
        try
        {
            var rows =
                archive is not null && key is not null
                    ? await Task.Run(() => archive.List(key, token), token)
                    : [];
            if (version != loadVersion || token.IsCancellationRequested)
                return;
            initializing = true;
            HistoryList.ItemsSource = rows.Select(row => new ManagedHistoryRow(row)).ToArray();
            foreach (ManagedHistoryRow row in HistoryList.Items)
                if (selected.Contains(row.Entry.Id))
                    HistoryList.SelectedItems.Add(row);
            initializing = false;
            UpdateSelection();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (version == loadVersion)
                ShowNotice(error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (version == loadVersion)
            {
                initializing = false;
                SetBusy(false);
            }
        }
    }

    private void UpdateSelection()
    {
        if (initializing)
            return;
        confirmed = null;
        ConfirmationPanel.Visibility = Visibility.Collapsed;
        Notice.IsOpen = false;
        int count = HistoryList.SelectedItems.Count;
        selectedIds = HistoryList
            .SelectedItems.Cast<ManagedHistoryRow>()
            .Select(row => row.Entry.Id)
            .ToHashSet();
        SelectionCountText.Text = $"選択中 {count:N0}件";
        TotalText.Text = $"{HistoryList.Items.Count:N0}件";
        EmptyText.Visibility =
            !busy && HistoryList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.IsEnabled = !busy && count > 0;
        UpdateSelectAllState();
    }

    private void UpdateSelectAllState()
    {
        int count = HistoryList.SelectedItems.Count;
        int total = HistoryList.Items.Count;
        SelectAllCheckBox.IsChecked =
            count == 0 ? false
            : count == total ? true
            : null;
        SelectAllCheckBox.IsEnabled = !busy && total > 0;
        string action = total > 0 && count == total ? "選択解除" : "すべて選択";
        ToolTipService.SetToolTip(SelectAllCheckBox, action);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SelectAllCheckBox, action);
    }

    private void ApplyRange()
    {
        if (initializing || busy)
            return;
        InvalidateConfirmation();
        try
        {
            DateTimeOffset? Instant(CalendarDatePicker date, TimePicker time) =>
                date.Date is { } value
                    ? HistoryDeletion.LocalInstant(
                        DateOnly.FromDateTime(value.DateTime),
                        TimeOnly.FromTimeSpan(time.Time),
                        TimeZoneInfo.Local
                    )
                    : null;
            var start = Instant(FromDate, FromTime);
            var end = Instant(EndDate, EndTime);
            var ids = HistoryDeletion
                .SelectRange(
                    HistoryList.Items.Cast<ManagedHistoryRow>().Select(row => row.Entry),
                    start,
                    end ?? DateTimeOffset.MaxValue
                )
                .Select(row => row.Id)
                .ToHashSet();
            initializing = true;
            HistoryList.SelectedItems.Clear();
            foreach (ManagedHistoryRow row in HistoryList.Items)
                if (ids.Contains(row.Entry.Id))
                    HistoryList.SelectedItems.Add(row);
            initializing = false;
            SelectionError.Visibility = Visibility.Collapsed;
            UpdateSelection();
        }
        catch (ArgumentException error)
        {
            // A rejected input does not replace the user's current manual selection.
            SelectionError.Text = error.Message;
            SelectionError.Visibility = Visibility.Visible;
        }
    }

    private void InvalidateConfirmation()
    {
        confirmed = null;
        ConfirmationPanel.Visibility = Visibility.Collapsed;
        DeleteButton.IsEnabled = !busy && HistoryList.SelectedItems.Count > 0;
    }

    private void UpdateRangeText()
    {
        static string Display(CalendarDatePicker date, TimePicker time, string placeholder) =>
            date.Date is { } value ? value.Date.Add(time.Time).ToString("g") : placeholder;
        FromValueText.Text = Display(FromDate, FromTime, "最初の記録");
        EndValueText.Text = Display(EndDate, EndTime, "最新の記録");
    }

    private void OnApplyRange(object sender, RoutedEventArgs args) => ApplyRange();

    private void OnClearRange(object sender, RoutedEventArgs args)
    {
        if (busy)
            return;
        initializing = true;
        FromDate.Date = EndDate.Date = null;
        FromTime.Time = EndTime.Time = TimeSpan.Zero;
        initializing = false;
        DatesEdited();
    }

    private void SetBusy(bool value)
    {
        busy = value;
        foreach (
            Control control in new Control[]
            {
                FromDate,
                FromTime,
                EndDate,
                EndTime,
                FromRangeButton,
                EndRangeButton,
                ClearRangeButton,
                ApplyRangeButton,
                MonthPresetButton,
                ThreeMonthsPresetButton,
                HalfYearPresetButton,
                YearPresetButton,
                HistoryList,
                RefreshButton,
                ConfirmDeleteButton,
                CancelDeleteButton,
            }
        )
            control.IsEnabled = !value;
        BusyRing.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        BusyRing.IsActive = value;
        EmptyText.Visibility =
            !value && HistoryList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.IsEnabled = !value && confirmed is null && HistoryList.SelectedItems.Count > 0;
        UpdateSelectAllState();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args) =>
        UpdateSelection();

    private void OnPresetClick(object sender, RoutedEventArgs args)
    {
        if (initializing || busy || sender is not Button { Tag: string tag })
            return;
        var cutoff = DateTimeOffset.Now.AddMonths(
            -int.Parse(tag, System.Globalization.CultureInfo.InvariantCulture)
        );
        initializing = true;
        FromDate.Date = null;
        EndDate.Date = cutoff;
        EndTime.Time = new(cutoff.Hour, cutoff.Minute, 0);
        initializing = false;
        DatesEdited();
    }

    private void DatesEdited()
    {
        if (initializing || busy)
            return;
        UpdateRangeText();
        InvalidateConfirmation();
        SelectionError.Visibility = Visibility.Collapsed;
    }

    private void OnDateChanged(
        CalendarDatePicker sender,
        CalendarDatePickerDateChangedEventArgs args
    ) => DatesEdited();

    private void OnTimeChanged(object sender, TimePickerValueChangedEventArgs args) =>
        DatesEdited();

    private void OnToggleAll(object sender, RoutedEventArgs args)
    {
        if (busy || HistoryList.Items.Count == 0)
            return;
        if (HistoryList.SelectedItems.Count == HistoryList.Items.Count)
            HistoryList.SelectedItems.Clear();
        else
            HistoryList.SelectAll();
        UpdateSelectAllState();
    }

    private void OnRefresh(object sender, RoutedEventArgs args) =>
        WorkStarted?.Invoke(LoadAsync(volumeKey));

    private void OnDelete(object sender, RoutedEventArgs args)
    {
        if (busy || HistoryList.SelectedItems.Count == 0)
            return;
        var rows = HistoryList
            .SelectedItems.Cast<ManagedHistoryRow>()
            .Select(row => row.Entry)
            .ToArray();
        confirmed = rows.Select(row => row.Id).ToArray();
        ConfirmationText.Text =
            $"{rows.Length:N0}件を削除: {rows.Min(row => row.CompletedUtc).ToLocalTime():yyyy/MM/dd HH:mm:ss} ～ {rows.Max(row => row.CompletedUtc).ToLocalTime():yyyy/MM/dd HH:mm:ss}";
        ConfirmationPanel.Visibility = Visibility.Visible;
        DeleteButton.IsEnabled = false;
    }

    private void OnCancelDelete(object sender, RoutedEventArgs args) => UpdateSelection();

    private void OnConfirmDelete(object sender, RoutedEventArgs args)
    {
        if (!busy && confirmed is { } ids)
            WorkStarted?.Invoke(DeleteConfirmedAsync(ids));
    }

    private async Task DeleteConfirmedAsync(Guid[] ids)
    {
        if (archive is null || refreshViews is null)
            return;
        IsDeleting = true;
        SetBusy(true);
        DeletingChanged?.Invoke(true);
        try
        {
            var removed = await Task.Run(() => archive.Delete(ids, token), token);
            await refreshViews(removed);
            int deferred = await Task.Run(() => archive.CollectRetired(token), token);
            IsDeleting = false;
            await LoadAsync(volumeKey);
            ShowNotice(
                $"{removed.Length:N0}件のスキャン履歴を削除しました。"
                    + (deferred > 0 ? $" 使用中の{deferred:N0}件は参照解放後に整理します。" : ""),
                InfoBarSeverity.Success
            );
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Retirement may already be committed; reconcile the visible view before reporting.
            try
            {
                await refreshViews(ids);
                IsDeleting = false;
                await LoadAsync(volumeKey);
            }
            catch (Exception refreshError)
            {
                ShowNotice(
                    $"{error.Message}\n一覧の再読み込みにも失敗しました: {refreshError.Message}",
                    InfoBarSeverity.Error
                );
                return;
            }
            ShowNotice($"削除後の状態を確認してください。{error.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsDeleting = false;
            SetBusy(false);
            DeletingChanged?.Invoke(false);
        }
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    internal async Task VerifySyntheticManagementAsync()
    {
        static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
        Require(
            HistoryList.Items.Count >= 2 && HistoryList.SelectedItems.Count == 0,
            "Management did not start unchecked."
        );
        Require(
            FromValueText.Text == "最初の記録" && EndValueText.Text == "最新の記録",
            "Range placeholders failed."
        );
        Require(
            SelectAllCheckBox.IsChecked == false && SelectAllCheckBox.IsEnabled,
            "Empty selection header failed."
        );
        OnToggleAll(this, new());
        Require(SelectAllCheckBox.IsChecked == true, "Header did not select all.");
        OnToggleAll(this, new());
        Require(HistoryList.SelectedItems.Count == 0, "Header did not clear all.");
        foreach (
            var (button, months) in new[]
            {
                (MonthPresetButton, 1),
                (ThreeMonthsPresetButton, 3),
                (HalfYearPresetButton, 6),
                (YearPresetButton, 12),
            }
        )
        {
            OnPresetClick(button, new());
            var expected = DateTimeOffset.Now.AddMonths(-months);
            Require(
                FromDate.Date is null
                    && EndDate.Date?.Date == expected.Date
                    && HistoryList.SelectedItems.Count == 0,
                "Preset did not only edit the range."
            );
        }
        HistoryList.SelectedItems.Add(HistoryList.Items[0]);
        var manual = selectedIds.ToHashSet();
        OnDelete(this, new());
        OnPresetClick(HalfYearPresetButton, new());
        Require(
            selectedIds.SetEquals(manual) && confirmed is null,
            "Preset changed manual checks or retained confirmation."
        );
        OnApplyRange(this, new());
        Require(
            HistoryList.SelectedItems.Count == 0,
            "Explicit range action did not replace checks."
        );
        FromDate.Date = DateTimeOffset.Now.AddYears(-2);
        EndDate.Date = DateTimeOffset.Now.AddDays(1);
        Require(HistoryList.SelectedItems.Count == 0, "Date editing changed checks before apply.");
        OnApplyRange(this, new());
        Require(
            HistoryList.SelectedItems.Count == HistoryList.Items.Count,
            "Explicit full interval failed."
        );
        OnDelete(this, new());
        HistoryList.SelectedItems.RemoveAt(0);
        Require(
            confirmed is null && SelectAllCheckBox.IsChecked is null,
            "Manual adjustment did not invalidate confirmation."
        );
        var selectedBeforeRefresh = selectedIds.ToHashSet();
        await Task.WhenAll(LoadAsync(volumeKey), LoadAsync(volumeKey));
        Require(
            selectedIds.SetEquals(selectedBeforeRefresh),
            "Overlapping refresh changed manual checks."
        );
        OnToggleAll(this, new());
        Require(SelectAllCheckBox.IsChecked == true, "Partial header did not select all.");
        OnPresetClick(HalfYearPresetButton, new());
        OnPresetClick(HalfYearPresetButton, new());
        Require(
            SelectAllCheckBox.IsChecked == true,
            "Repeated preset changed checks before apply."
        );
        OnApplyRange(this, new());
        Require(HistoryList.SelectedItems.Count == 0, "Preset range was not applied explicitly.");
        EndDate.Date = DateTimeOffset.Now.AddDays(1);
        OnApplyRange(this, new());
        var beforeInvalid = selectedIds.ToHashSet();
        FromDate.Date = DateTimeOffset.Now.AddDays(2);
        OnApplyRange(this, new());
        Require(
            selectedIds.SetEquals(beforeInvalid) && SelectionError.Visibility == Visibility.Visible,
            "Rejected interval destroyed checks."
        );
        OnClearRange(this, new());
        Require(
            FromDate.Date is null && EndDate.Date is null && selectedIds.SetEquals(beforeInvalid),
            "Clearing input changed checks."
        );
        HistoryList.SelectedItems.RemoveAt(0);
        OnApplyRange(this, new());
        Require(
            HistoryList.SelectedItems.Count == HistoryList.Items.Count,
            "Earliest-to-latest range did not select all."
        );
        HistoryList.SelectedItems.Clear();
        HistoryList.SelectedItems.Add(HistoryList.Items[0]);
        OnPresetClick(HalfYearPresetButton, new());
        OnDelete(this, new());
        var first =
            confirmed ?? throw new InvalidOperationException("Manual deletion not confirmed.");
        Require(first.Length == 1, "Deletion used the input range instead of checked rows.");
        await DeleteConfirmedAsync(first);
        Require(
            Notice.Severity == InfoBarSeverity.Success
                && HistoryList.Items.Count > 0
                && HistoryList
                    .Items.Cast<ManagedHistoryRow>()
                    .All(row => !first.Contains(row.Entry.Id)),
            "Manual deletion failed."
        );
        OnToggleAll(this, new());
        OnDelete(this, new());
        await DeleteConfirmedAsync(confirmed!);
        Require(
            HistoryList.Items.Count == 0
                && Notice.Severity == InfoBarSeverity.Success
                && !SelectAllCheckBox.IsEnabled
                && SelectAllCheckBox.IsChecked == false,
            "Empty drive retained history or header state."
        );
    }

    internal bool IsShowingVolume(string key) =>
        volumeKey == key
        && HistoryList.Items.Count > 0
        && HistoryList.Items.Cast<ManagedHistoryRow>().All(row => row.Entry.VolumeKey == key);
}
