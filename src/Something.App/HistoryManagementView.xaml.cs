using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Something.Storage;

namespace Something.App;

internal sealed record ManagedHistoryRow(ScanHistoryEntry Entry, long RecordCount)
{
    public string CompletedText => Entry.CompletedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");
    public string RecordCountText => RecordCount.ToString("N0");
}

public sealed partial class HistoryManagementView : UserControl
{
    private ScanHistoryArchive? archive;
    private Func<Guid[], Task>? refreshViews;
    private CancellationToken token;
    private string? volumeKey;
    private HashSet<Guid> selectedIds = [];
    private HistoryDeleteDialog? activeDeleteDialog;
    private Func<Task<HistoryCompactionResult>>? optimize;
    private Task currentOperation = Task.CompletedTask;
    private readonly DispatcherTimer noticeTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private int loadVersion;
    private bool initializing = true;
    private bool busy;
    private bool operationActive;
    internal event Action<Task>? WorkStarted;
    internal event Action<bool>? MaintenanceChanged;

    public HistoryManagementView()
    {
        InitializeComponent();
        noticeTimer.Tick += (_, _) => DismissNotice();
        Unloaded += (_, _) => DismissNotice();
        FromTime.Time = TimeSpan.Zero;
        EndTime.Time = TimeSpan.Zero;
        initializing = false;
        UpdateRangeText();
    }

    internal void Configure(
        ScanHistoryArchive? archive,
        Func<Guid[], Task> refreshViews,
        Func<Task<HistoryCompactionResult>> optimize,
        CancellationToken token
    )
    {
        this.archive = archive;
        this.refreshViews = refreshViews;
        this.optimize = optimize;
        this.token = token;
    }

    internal async Task LoadAsync(string? key)
    {
        if (operationActive)
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
        DismissNotice();
        SetBusy(true);
        try
        {
            var rows =
                archive is not null && key is not null
                    ? await Task.Run(() => archive.ListSummaries(key, token), token)
                    : [];
            if (version != loadVersion || token.IsCancellationRequested)
                return;
            initializing = true;
            HistoryList.ItemsSource = rows.Select(row => new ManagedHistoryRow(
                    row.Entry,
                    row.RecordCount
                ))
                .ToArray();
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
                OptimizeButton,
            }
        )
            control.IsEnabled = !value;
        BusyRing.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        BusyRing.IsActive = value;
        EmptyText.Visibility =
            !value && HistoryList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.IsEnabled = !value && HistoryList.SelectedItems.Count > 0;
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

    private sealed record OperationNotice(string Text, InfoBarSeverity Severity);

    private void OnDelete(object sender, RoutedEventArgs args)
    {
        if (busy || HistoryList.SelectedItems.Count == 0)
            return;
        var rows = HistoryList
            .SelectedItems.Cast<ManagedHistoryRow>()
            .Select(row => row.Entry)
            .ToArray();
        StartOperation(async () =>
        {
            var dialog = new HistoryDeleteDialog(rows) { XamlRoot = XamlRoot };
            activeDeleteDialog = dialog;
            ContentDialogResult result;
            try
            {
                result = await dialog.ShowAsync();
            }
            finally
            {
                activeDeleteDialog = null;
            }
            token.ThrowIfCancellationRequested();
            if (result != ContentDialogResult.Primary)
                return null;
            return await DeleteCoreAsync(rows.Select(row => row.Id).ToArray(), dialog.Optimize);
        });
    }

    private void OnOptimize(object sender, RoutedEventArgs args)
    {
        if (busy)
            return;
        StartOperation(async () =>
            new OperationNotice(OptimizationText(await optimize!()), InfoBarSeverity.Success)
        );
    }

    private void StartOperation(Func<Task<OperationNotice?>> action)
    {
        currentOperation = RunOperationAsync(action);
        WorkStarted?.Invoke(currentOperation);
    }

    private async Task RunOperationAsync(Func<Task<OperationNotice?>> action)
    {
        operationActive = true;
        SetBusy(true);
        MaintenanceChanged?.Invoke(true);
        OperationNotice? notice = null;
        try
        {
            notice = await action();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            notice = new(error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            operationActive = false;
            try
            {
                if (!token.IsCancellationRequested)
                    await LoadAsync(volumeKey);
            }
            finally
            {
                SetBusy(false);
                MaintenanceChanged?.Invoke(false);
            }
        }
        if (notice is not null && !token.IsCancellationRequested)
            ShowNotice(notice.Text, notice.Severity);
    }

    private async Task<OperationNotice> DeleteCoreAsync(Guid[] ids, bool optimizeAfter)
    {
        var removed = await Task.Run(() => archive!.Delete(ids, token), token);
        string completed = $"{removed.Length:N0}件のスキャン履歴を削除しました。";
        try
        {
            await refreshViews!(removed);
            int deferred = await Task.Run(() => archive!.CollectRetired(token), token);
            if (deferred > 0)
                completed += $" 使用中の{deferred:N0}件は参照解放後に整理します。";
            if (optimizeAfter)
                return new(
                    completed + " " + OptimizationText(await optimize!()),
                    InfoBarSeverity.Success
                );
            await Task.Run(() => archive!.Checkpoint(token), token);
            return new(completed, InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // Deletion has committed. A failed optimization never means deletion was rolled back.
            try
            {
                await refreshViews!(removed);
            }
            catch (Exception refreshError)
            {
                return new(
                    $"{completed} 表示の再読み込みに失敗しました: {refreshError.Message}",
                    InfoBarSeverity.Warning
                );
            }
            return new(
                $"{completed} {(optimizeAfter ? "DB最適化" : "削除後の整理")}を完了できませんでした: {error.Message}",
                InfoBarSeverity.Warning
            );
        }
    }

    private static string OptimizationText(HistoryCompactionResult result) =>
        result.Replaced
            ? $"DBを最適化しました。{DisplayText.Bytes((UInt128)result.BeforeBytes)} → {DisplayText.Bytes((UInt128)result.AfterBytes)}（{DisplayText.Bytes((UInt128)result.ReclaimedBytes)}削減）"
            : "DBを確認しました。詰め直しによる縮小がないため、元のDBを維持しました。";

    internal void CloseForShutdown()
    {
        DismissNotice();
        activeDeleteDialog?.Hide();
    }

    internal async Task VerifyStandaloneOptimizationAsync()
    {
        OnOptimize(this, new());
        await currentOperation;
        if (Notice.Severity != InfoBarSeverity.Success)
            throw new InvalidOperationException(
                "Standalone DB optimization failed: " + Notice.Message
            );
        UpdateSelection();
        if (!Notice.IsOpen || !noticeTimer.IsEnabled)
            throw new InvalidOperationException(
                "Completion toast did not survive selection updates."
            );
        await Task.Delay(TimeSpan.FromSeconds(6.2), token);
        if (Notice.IsOpen || noticeTimer.IsEnabled)
            throw new InvalidOperationException("Completion toast did not dismiss automatically.");
    }

    internal async Task<ContentDialog> PreviewSyntheticDeleteDialogAsync()
    {
        var rows = HistoryList
            .Items.Cast<ManagedHistoryRow>()
            .Take(3)
            .Select(row => row.Entry)
            .ToArray();
        var dialog = new HistoryDeleteDialog(rows) { XamlRoot = XamlRoot };
        activeDeleteDialog = dialog;
        _ = dialog.ShowAsync();
        await Task.Delay(300, token);
        return dialog;
    }

    private async Task VerifySyntheticDeletionAsync(bool confirm, bool optimizeAfter = false)
    {
        OnDelete(this, new());
        await Task.Delay(200, token);
        var dialog =
            activeDeleteDialog
            ?? throw new InvalidOperationException("Delete confirmation dialog was not shown.");
        dialog.VerifyDefaultOptions();
        if (confirm)
            dialog.ConfirmSynthetic(optimizeAfter);
        else
            dialog.Hide();
        await currentOperation;
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        noticeTimer.Stop();
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
        // Keep failures reviewable; only successful completion disappears automatically.
        if (severity == InfoBarSeverity.Success)
            noticeTimer.Start();
    }

    private void DismissNotice()
    {
        noticeTimer.Stop();
        Notice.IsOpen = false;
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
        await VerifySyntheticDeletionAsync(false);
        OnPresetClick(HalfYearPresetButton, new());
        Require(
            selectedIds.SetEquals(manual),
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
        HistoryList.SelectedItems.RemoveAt(0);
        Require(
            SelectAllCheckBox.IsChecked is null,
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
        var first = selectedIds.ToHashSet();
        await VerifySyntheticDeletionAsync(true, optimizeAfter: true);
        Require(
            Notice.Severity == InfoBarSeverity.Success
                && HistoryList.Items.Count > 0
                && HistoryList
                    .Items.Cast<ManagedHistoryRow>()
                    .All(row => !first.Contains(row.Entry.Id)),
            "Manual deletion with optimization failed."
        );
        OnToggleAll(this, new());
        await VerifySyntheticDeletionAsync(true);
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
