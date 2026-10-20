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
    private HistoryOptimizeDialog? activeOptimizeDialog;
    private Func<Task<HistoryCompactionResult>>? optimize;
    private Func<string, LoadingAnimationKind, IAsyncDisposable>? beginProgress;
    private Task currentOperation = Task.CompletedTask;
    private readonly DispatcherTimer noticeTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private int loadVersion;
    private bool initializing = true;
    private bool busy;
    private bool operationActive;
    private bool sortByRecordCount;
    private bool sortDescending = true;
    private bool automaticStart = true;
    private bool automaticEnd = true;
    private DateTimeOffset? defaultStart;
    private DateTimeOffset? defaultEnd;
    internal event Action<Task>? WorkStarted;
    internal event Action<bool>? MaintenanceChanged;

    public HistoryManagementView()
    {
        InitializeComponent();
        UpdateHistorySortHeaders();
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
        Func<string, LoadingAnimationKind, IAsyncDisposable> beginProgress,
        CancellationToken token
    )
    {
        this.archive = archive;
        this.refreshViews = refreshViews;
        this.optimize = optimize;
        this.beginProgress = beginProgress;
        this.token = token;
        RefreshDatabaseSize();
    }

    internal void RefreshDatabaseSize()
    {
        try
        {
            if (archive is null)
            {
                DatabaseSizeText.Text = "";
                return;
            }
            var info = archive.ReadDatabaseInfo();
            DatabaseSizeText.Text = $"データベース {DisplayText.Bytes((UInt128)info.TotalBytes)}";
            ToolTipService.SetToolTip(
                DatabaseSizeText,
                $"全ドライブ共通の履歴データベース\n{info.Path}"
            );
        }
        catch (Exception error)
            when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DatabaseSizeText.Text = "データベースのサイズを取得できません";
            ToolTipService.SetToolTip(DatabaseSizeText, error.Message);
        }
    }

    internal Task LoadAsync(string? key) =>
        operationActive ? Task.CompletedTask : LoadCoreAsync(key);

    private async Task LoadCoreAsync(string? key)
    {
        int version = ++loadVersion;
        bool changed = volumeKey != key;
        var selected = changed ? [] : selectedIds.ToHashSet();
        volumeKey = key;
        initializing = true;
        if (changed)
        {
            selectedIds.Clear();
            automaticStart = automaticEnd = true;
            defaultStart = defaultEnd = null;
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
            HistoryList.ItemsSource = OrderHistory(
                rows.Select(row => new ManagedHistoryRow(row.Entry, row.RecordCount))
            );
            UpdateDefaultRange();
            foreach (ManagedHistoryRow row in HistoryList.Items)
                if (selected.Contains(row.Entry.Id))
                    HistoryList.SelectedItems.Add(row);
            initializing = false;
            UpdateRangeText();
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
                RefreshDatabaseSize();
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

    private ManagedHistoryRow[] OrderHistory(IEnumerable<ManagedHistoryRow> rows)
    {
        var ordered = sortByRecordCount
            ? sortDescending
                ? rows.OrderByDescending(row => row.RecordCount)
                : rows.OrderBy(row => row.RecordCount)
            : sortDescending
                ? rows.OrderByDescending(row => row.Entry.CompletedUtc)
                : rows.OrderBy(row => row.Entry.CompletedUtc);
        return [.. ordered.ThenBy(row => row.Entry.CompletedUtc).ThenBy(row => row.Entry.Id)];
    }

    private void UpdateHistorySortHeaders()
    {
        ColumnHeaders.Update(CompletedHeader, "スキャン日時", !sortByRecordCount, sortDescending);
        ColumnHeaders.Update(RecordCountHeader, "レコード数", sortByRecordCount, sortDescending);
    }

    private void OnHistorySort(object sender, RoutedEventArgs args)
    {
        if (busy || sender is not Button { Tag: string tag })
            return;
        bool byCount = tag == "Count";
        sortDescending = byCount == sortByRecordCount ? !sortDescending : true;
        sortByRecordCount = byCount;
        var selected = selectedIds.ToHashSet();
        var rows = OrderHistory(HistoryList.Items.Cast<ManagedHistoryRow>());
        initializing = true;
        HistoryList.ItemsSource = rows;
        foreach (var row in rows)
            if (selected.Contains(row.Entry.Id))
                HistoryList.SelectedItems.Add(row);
        initializing = false;
        UpdateHistorySortHeaders();
        UpdateSelection();
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
            var start = automaticStart ? defaultStart : Instant(FromDate, FromTime);
            var end = automaticEnd ? defaultEnd : Instant(EndDate, EndTime);
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

    private void UpdateDefaultRange()
    {
        var instants = HistoryList
            .Items.Cast<ManagedHistoryRow>()
            .Select(row => (DateTimeOffset?)row.Entry.CompletedUtc)
            .ToArray();
        defaultStart = instants.Min();
        defaultEnd = instants.Max();
        if (automaticStart)
        {
            FromDate.Date = defaultStart?.ToLocalTime();
            FromTime.Time = defaultStart?.ToLocalTime().TimeOfDay ?? TimeSpan.Zero;
        }
        if (automaticEnd)
        {
            EndDate.Date = defaultEnd?.ToLocalTime();
            EndTime.Time = defaultEnd?.ToLocalTime().TimeOfDay ?? TimeSpan.Zero;
        }
    }

    private void UpdateRangeText()
    {
        static string Display(CalendarDatePicker date, TimePicker time, DateTimeOffset? exact) =>
            exact?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss")
            ?? (
                date.Date is { } value
                    ? value.Date.Add(time.Time).ToString("yyyy/MM/dd HH:mm:ss")
                    : "—"
            );
        FromValueText.Text = Display(FromDate, FromTime, automaticStart ? defaultStart : null);
        EndValueText.Text = Display(EndDate, EndTime, automaticEnd ? defaultEnd : null);
    }

    private void OnApplyRange(object sender, RoutedEventArgs args) => ApplyRange();

    private void OnClearRange(object sender, RoutedEventArgs args)
    {
        if (busy)
            return;
        initializing = true;
        automaticStart = automaticEnd = false;
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
                CompletedHeader,
                RecordCountHeader,
            }
        )
            control.IsEnabled = !value;
        bool showSpinner = value && !operationActive;
        BusyRing.Visibility = showSpinner ? Visibility.Visible : Visibility.Collapsed;
        BusyRing.IsActive = showSpinner;
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
        automaticStart = automaticEnd = false;
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
    )
    {
        if (!initializing && !busy)
        {
            if (sender == FromDate)
                automaticStart = false;
            else
                automaticEnd = false;
        }
        DatesEdited();
    }

    private void OnTimeChanged(object sender, TimePickerValueChangedEventArgs args)
    {
        if (!initializing && !busy)
        {
            if (ReferenceEquals(sender, FromTime))
                automaticStart = false;
            else
                automaticEnd = false;
        }
        DatesEdited();
    }

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
        StartOperation(async showProgress =>
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
            showProgress("スキャン履歴を削除しています…", LoadingAnimationKind.DeleteHistory);
            return await DeleteCoreAsync(
                rows.Select(row => row.Id).ToArray(),
                dialog.Optimize,
                showProgress
            );
        });
    }

    private void OnOptimize(object sender, RoutedEventArgs args)
    {
        if (busy)
            return;
        StartOperation(async showProgress =>
        {
            var dialog = new HistoryOptimizeDialog { XamlRoot = XamlRoot };
            activeOptimizeDialog = dialog;
            ContentDialogResult result;
            try
            {
                result = await dialog.ShowAsync();
            }
            finally
            {
                activeOptimizeDialog = null;
            }
            token.ThrowIfCancellationRequested();
            if (result != ContentDialogResult.Primary)
                return null;
            showProgress("データベースを最適化しています…", LoadingAnimationKind.OptimizeDatabase);
            return new OperationNotice(
                OptimizationText(await optimize!()),
                InfoBarSeverity.Success
            );
        });
    }

    private void StartOperation(
        Func<Action<string, LoadingAnimationKind>, Task<OperationNotice?>> action
    )
    {
        currentOperation = RunOperationAsync(action);
        WorkStarted?.Invoke(currentOperation);
    }

    private async Task RunOperationAsync(
        Func<Action<string, LoadingAnimationKind>, Task<OperationNotice?>> action
    )
    {
        operationActive = true;
        SetBusy(true);
        MaintenanceChanged?.Invoke(true);
        OperationNotice? notice = null;
        List<IAsyncDisposable> presentations = [];
        try
        {
            notice = await action(
                (message, kind) => presentations.Add(beginProgress!(message, kind))
            );
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            notice = new(error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            try
            {
                if (!token.IsCancellationRequested)
                    await LoadCoreAsync(volumeKey);
            }
            finally
            {
                try
                {
                    // Cover deletion, reference refresh, collection, optional compaction,
                    // and the final list reload with one continuous animation scope.
                    for (int index = presentations.Count - 1; index >= 0; index--)
                        await presentations[index].DisposeAsync();
                }
                finally
                {
                    operationActive = false;
                    SetBusy(false);
                    MaintenanceChanged?.Invoke(false);
                }
            }
        }
        if (notice is not null && !token.IsCancellationRequested)
            ShowNotice(notice.Text, notice.Severity);
    }

    private async Task<OperationNotice> DeleteCoreAsync(
        Guid[] ids,
        bool optimizeAfter,
        Action<string, LoadingAnimationKind> showProgress
    )
    {
        var removed = await Task.Run(() => archive!.Delete(ids, token), token);
        string completed = $"{removed.Length:N0}件のスキャン履歴を削除しました。";
        try
        {
            await refreshViews!(removed);
            int deferred = await Task.Run(() => archive!.CollectRetired(token), token);
            if (deferred > 0)
                completed += $" 表示中の{deferred:N0}件は、表示を切り替えた後に削除されます。";
            if (optimizeAfter)
            {
                showProgress(
                    "データベースを最適化しています…",
                    LoadingAnimationKind.OptimizeDatabase
                );
                return new(
                    completed + " " + OptimizationText(await optimize!()),
                    InfoBarSeverity.Success
                );
            }
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
                    $"{completed} 表示の再読み込みに失敗しました：{refreshError.Message}",
                    InfoBarSeverity.Warning
                );
            }
            return new(
                $"{completed} {(optimizeAfter ? "データベースの最適化" : "削除後の整理")}を完了できませんでした：{error.Message}",
                InfoBarSeverity.Warning
            );
        }
    }

    private static string OptimizationText(HistoryCompactionResult result) =>
        result.Replaced
            ? $"データベースを最適化しました。{DisplayText.Bytes((UInt128)result.BeforeBytes)} → {DisplayText.Bytes((UInt128)result.AfterBytes)}（{DisplayText.Bytes((UInt128)result.ReclaimedBytes)}削減）"
            : "データベースはすでに最適化されています。";

    internal void CloseForShutdown()
    {
        DismissNotice();
        activeDeleteDialog?.Hide();
        activeOptimizeDialog?.Hide();
    }

    internal async Task VerifyStandaloneOptimizationAsync()
    {
        var originalOptimize = optimize!;
        int calls = 0;
        optimize = () =>
        {
            calls++;
            return originalOptimize();
        };
        try
        {
            OnOptimize(this, new());
            await Task.Delay(200, token);
            var cancelled =
                activeOptimizeDialog
                ?? throw new InvalidOperationException("Optimization confirmation was not shown.");
            if (calls != 0 || cancelled.DefaultButton != ContentDialogButton.None)
                throw new InvalidOperationException(
                    "Optimization started before explicit confirmation."
                );
            cancelled.Hide();
            await currentOperation;
            if (calls != 0)
                throw new InvalidOperationException("Cancelled optimization modified the DB.");
            OnOptimize(this, new());
            await Task.Delay(200, token);
            (
                activeOptimizeDialog
                ?? throw new InvalidOperationException("Optimization confirmation was not shown.")
            ).ConfirmSynthetic();
            await currentOperation;
            if (calls != 1)
                throw new InvalidOperationException(
                    "Confirmed optimization must run exactly once."
                );
        }
        finally
        {
            optimize = originalOptimize;
        }
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
        await dialog.VerifyButtonStatesAsync(token);
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
        Notice.Title = ToastPresentation.GetTitle(severity);
        Notice.Severity = severity;
        Notice.Style = ToastPresentation.GetStyle(severity);
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
            FromValueText.Text
                == HistoryList
                    .Items.Cast<ManagedHistoryRow>()
                    .MinBy(row => row.Entry.CompletedUtc)!
                    .CompletedText
                && EndValueText.Text
                    == HistoryList
                        .Items.Cast<ManagedHistoryRow>()
                        .MaxBy(row => row.Entry.CompletedUtc)!
                        .CompletedText
                && FromDate.Date is not null
                && EndDate.Date is not null,
            "History range did not default to the saved scan timestamps."
        );
        OnApplyRange(this, new());
        Require(
            HistoryList.SelectedItems.Count == HistoryList.Items.Count,
            "Default range excluded a boundary scan."
        );
        OnToggleAll(this, new());
        await LoadAsync(volumeKey);
        Require(automaticStart && automaticEnd, "Reload discarded automatic range defaults.");
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
        OnHistorySort(RecordCountHeader, new());
        var counts = HistoryList
            .Items.Cast<ManagedHistoryRow>()
            .Select(row => row.RecordCount)
            .ToArray();
        if (!counts.SequenceEqual(counts.OrderDescending()) || !selectedIds.SetEquals(manual))
            throw new InvalidOperationException("History count sorting lost order or selection.");
        OnHistorySort(RecordCountHeader, new());
        counts = HistoryList
            .Items.Cast<ManagedHistoryRow>()
            .Select(row => row.RecordCount)
            .ToArray();
        if (!counts.SequenceEqual(counts.Order()) || !selectedIds.SetEquals(manual))
            throw new InvalidOperationException(
                "Ascending history sorting lost order or selection."
            );
        OnHistorySort(CompletedHeader, new());
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
