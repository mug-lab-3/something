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
            PresetBox.SelectedIndex = -1;
        }
        HistoryList.ItemsSource = null;
        initializing = false;
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
            var ids =
                start is null && end is null
                    ? []
                    : HistoryDeletion
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
            // Invalid criteria must not leave an earlier selection armed for deletion.
            HistoryList.SelectedItems.Clear();
            UpdateSelection();
            SelectionError.Text = error.Message;
            SelectionError.Visibility = Visibility.Visible;
        }
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
                PresetBox,
                SelectAllButton,
                ClearSelectionButton,
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
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args) =>
        UpdateSelection();

    private void OnPresetChanged(object sender, SelectionChangedEventArgs args)
    {
        if (initializing || busy || PresetBox.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;
        var cutoff = DateTimeOffset.Now.AddMonths(
            -int.Parse(tag, System.Globalization.CultureInfo.InvariantCulture)
        );
        initializing = true;
        FromDate.Date = null;
        EndDate.Date = cutoff;
        EndTime.Time = new(cutoff.Hour, cutoff.Minute, 0);
        initializing = false;
        ApplyRange();
    }

    private void DatesEdited()
    {
        if (initializing || busy)
            return;
        initializing = true;
        PresetBox.SelectedIndex = -1;
        initializing = false;
        ApplyRange();
    }

    private void OnDateChanged(
        CalendarDatePicker sender,
        CalendarDatePickerDateChangedEventArgs args
    ) => DatesEdited();

    private void OnTimeChanged(object sender, TimePickerValueChangedEventArgs args) =>
        DatesEdited();

    private void OnSelectAll(object sender, RoutedEventArgs args) => HistoryList.SelectAll();

    private void OnClearSelection(object sender, RoutedEventArgs args) =>
        HistoryList.SelectedItems.Clear();

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
            "Management did not start with unchecked history."
        );
        PresetBox.SelectedIndex = 2;
        Require(
            FromDate.Date is null
                && EndDate.Date?.Month == DateTimeOffset.Now.AddMonths(-6).Month
                && HistoryList.SelectedItems.Count == 0,
            "Six month cutoff preset failed."
        );
        PresetBox.SelectedIndex = 3;
        Require(
            EndDate.Date?.Year == DateTimeOffset.Now.AddYears(-1).Year,
            "Year cutoff preset failed."
        );
        FromDate.Date = DateTimeOffset.Now.AddYears(-2);
        EndDate.Date = DateTimeOffset.Now.AddDays(1);
        Require(
            HistoryList.SelectedItems.Count == HistoryList.Items.Count
                && PresetBox.SelectedIndex == -1,
            "Range did not check all matching rows."
        );
        OnDelete(this, new());
        Require(confirmed?.Length == HistoryList.Items.Count, "Confirmation was not frozen.");
        HistoryList.SelectedItems.RemoveAt(0);
        Require(
            confirmed is null && HistoryList.SelectedItems.Count == HistoryList.Items.Count - 1,
            "Manual unchecking did not cancel confirmation."
        );
        var selectedBeforeRefresh = selectedIds.ToHashSet();
        await Task.WhenAll(LoadAsync(volumeKey), LoadAsync(volumeKey));
        Require(
            selectedIds.SetEquals(selectedBeforeRefresh),
            "Overlapping refreshes changed manual selection."
        );
        FromDate.Date = DateTimeOffset.Now.AddDays(2);
        Require(
            HistoryList.SelectedItems.Count == 0
                && !DeleteButton.IsEnabled
                && SelectionError.Visibility == Visibility.Visible,
            "Invalid interval left an armed selection."
        );
        FromDate.Date = null;
        Require(
            HistoryList.SelectedItems.Count == HistoryList.Items.Count,
            "Before-only interval failed."
        );
        HistoryList.SelectedItems.Clear();
        HistoryList.SelectedItems.Add(HistoryList.Items[0]);
        OnDelete(this, new());
        var first =
            confirmed ?? throw new InvalidOperationException("Selected deletion not confirmed.");
        await DeleteConfirmedAsync(first);
        Require(
            Notice.Severity == InfoBarSeverity.Success
                && HistoryList.Items.Count > 0
                && HistoryList
                    .Items.Cast<ManagedHistoryRow>()
                    .All(row => !first.Contains(row.Entry.Id)),
            "Selected deletion failed."
        );
        HistoryList.SelectAll();
        OnDelete(this, new());
        await DeleteConfirmedAsync(confirmed!);
        Require(
            HistoryList.Items.Count == 0 && Notice.Severity == InfoBarSeverity.Success,
            "Empty drive retained another drive's rows."
        );
    }

    internal bool IsShowingVolume(string key) =>
        volumeKey == key
        && HistoryList.Items.Count > 0
        && HistoryList.Items.Cast<ManagedHistoryRow>().All(row => row.Entry.VolumeKey == key);
}
