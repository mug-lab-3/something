using System.Collections.Immutable;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Something.Application;
using Something.Ntfs;
using Something.Storage;

namespace Something.App;

public sealed partial class MainWindow
{
    private sealed record PeriodChoice(string Label, FolderChangePeriod Period)
    {
        public override string ToString() => Label;
    }

    private bool updatingComparisonChoices;
    private bool applyingComparison;
    private int comparisonVersion;
    private CancellationTokenSource? comparisonCancellation;
    private ScanSession? comparisonBaseline;
    private FolderChangeReport? comparisonReport;
    private Dictionary<FileReference, FolderChange> comparisonFolders = [];
    private FileReference? cachedComparisonFolder;
    private ImmutableArray<EntryChange> cachedComparisonEntries = [];
    private bool Comparing => ComparisonModeBox.SelectedIndex == 1;
    private (
        FolderChangePeriod Period,
        Guid? Before,
        Guid? After,
        string? Drive,
        bool Partial
    )? comparisonSelectionKey;

    private bool ComparisonNeedsRefresh()
    {
        var period =
            (ComparisonPeriodBox.SelectedItem as PeriodChoice)?.Period
            ?? FolderChangePeriod.Last24Hours;
        Guid? before;
        Guid? after;
        bool partial = false;
        if (period == FolderChangePeriod.Custom)
        {
            before = (ComparisonBeforeBox.SelectedItem as HistoryRow)?.Entry.Id;
            after = (ComparisonAfterBox.SelectedItem as HistoryRow)?.Entry.Id;
        }
        else
        {
            var rows =
                (ComparisonAfterBox.ItemsSource as IEnumerable<HistoryRow>)?.Select(row =>
                    row.Entry
                ) ?? [];
            var selected = ComparisonSelection.Resolve(
                rows,
                period,
                DateTimeOffset.UtcNow,
                TimeZoneInfo.Local
            );
            before = selected.Before?.Id;
            after = selected.After?.Id;
            partial = selected.IsPartial;
        }
        return comparisonSelectionKey
            != (period, before, after, selectedCard?.Choice.Drive, partial);
    }

    private void InitializeComparisonControls()
    {
        updatingComparisonChoices = true;
        try
        {
            ComparisonModeBox.ItemsSource = new[] { "通常", "差分" };
            ComparisonModeBox.SelectedIndex = 0;
            ComparisonPeriodBox.ItemsSource = new PeriodChoice[]
            {
                new("24時間", FolderChangePeriod.Last24Hours),
                new("7日間", FolderChangePeriod.Last7Days),
                new("30日間", FolderChangePeriod.Last30Days),
                new("365日間", FolderChangePeriod.Last365Days),
                new("今日", FolderChangePeriod.Today),
                new("今週", FolderChangePeriod.ThisWeek),
                new("今月", FolderChangePeriod.ThisMonth),
                new("今年", FolderChangePeriod.ThisYear),
                new("任意の2スキャン", FolderChangePeriod.Custom),
            };
            ComparisonPeriodBox.SelectedIndex = 0;
        }
        finally
        {
            updatingComparisonChoices = false;
        }
    }

    private void UpdateComparisonChoices(IReadOnlyList<HistoryRow> rows)
    {
        var beforeId = (ComparisonBeforeBox.SelectedItem as HistoryRow)?.Entry.Id;
        var afterId = (ComparisonAfterBox.SelectedItem as HistoryRow)?.Entry.Id;
        updatingComparisonChoices = true;
        try
        {
            ComparisonBeforeBox.ItemsSource = rows;
            ComparisonAfterBox.ItemsSource = rows;
            ComparisonBeforeBox.SelectedItem = beforeId is null
                ? rows.LastOrDefault()
                : rows.FirstOrDefault(row => row.Entry.Id == beforeId);
            ComparisonAfterBox.SelectedItem = afterId is null
                ? rows.FirstOrDefault()
                : rows.FirstOrDefault(row => row.Entry.Id == afterId);
        }
        finally
        {
            updatingComparisonChoices = false;
        }
    }

    private async void OnComparisonOptionsChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!updatingComparisonChoices && !closed)
            await TrackComparisonAsync();
    }

    private async Task TrackComparisonAsync()
    {
        var task = RefreshComparisonAsync();
        historyTasks.Add(task);
        try
        {
            await task;
        }
        finally
        {
            historyTasks.Remove(task);
        }
    }

    private async Task<int> ClearComparisonAsync()
    {
        int version = ++comparisonVersion;
        navigationVersion++;
        rankingVersion++;
        applyingComparison = false;
        comparisonCancellation?.Cancel();
        listingCancellation?.Cancel();
        comparisonReport = null;
        comparisonSelectionKey = null;
        ComparisonStatusText.Text = "比較するにはスキャン履歴が必要です。";
        SelectedBeforeText.Text = "比較前：—";
        SelectedAfterText.Text = "比較後：—";
        comparisonFolders = [];
        cachedComparisonFolder = null;
        cachedComparisonEntries = [];
        var previous = comparisonBaseline;
        comparisonBaseline = null;
        if (previous is not null)
            await previous.DisposeAsync();
        return version;
    }

    private void UpdateComparisonLayout()
    {
        bool custom =
            ComparisonPeriodBox.SelectedItem is PeriodChoice { Period: FolderChangePeriod.Custom };
        ComparisonPeriodBox.Visibility = Comparing ? Visibility.Visible : Visibility.Collapsed;
        CustomComparisonPanel.Visibility =
            Comparing && custom ? Visibility.Visible : Visibility.Collapsed;
        ComparisonStatusText.Visibility = Comparing ? Visibility.Visible : Visibility.Collapsed;
        ComparisonRangePanel.Visibility = Comparing ? Visibility.Visible : Visibility.Collapsed;
        ChangeOrderBox.Visibility = Comparing ? Visibility.Visible : Visibility.Collapsed;
        BeforeSizeHeader.Visibility = AfterSizeHeader.Visibility = Comparing
            ? Visibility.Visible
            : Visibility.Collapsed;
        BeforeSizeHeaderColumn.Width = AfterSizeHeaderColumn.Width = new(Comparing ? 120 : 0);
        SizeColumnText.Text = Comparing ? "増減" : "サイズ";
        RankingTab.Header = Comparing ? "変動ランキング" : "容量ランキング";
        RankingBeforeColumn.Width = RankingAfterColumn.Width = new(Comparing ? 120 : 0);
        RankingBeforeHeader.Visibility = RankingAfterHeader.Visibility = Comparing
            ? Visibility.Visible
            : Visibility.Collapsed;
        RankingSizeHeader.Text = Comparing ? "増減" : "サイズ";
        HistoryBox.IsEnabled = !Comparing && HistoryBox.Items.Count > 0;
        HistoryBox.Visibility =
            Comparing || demo && history is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RefreshComparisonAsync()
    {
        UpdateComparisonLayout();
        int version = await ClearComparisonAsync();
        if (closed || version != comparisonVersion)
            return;
        comparisonCancellation?.Dispose();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        comparisonCancellation = cancellation;
        var token = cancellation.Token;
        if (closed || session is not { } displayed || selectedCard is not { } card)
        {
            ComparisonStatusText.Text = "比較するにはスキャン履歴が必要です。";
            return;
        }
        if (!Comparing)
        {
            comparisonSelectionKey = null;
            await ShowSessionAsync(displayed);
            return;
        }
        ScanSession? owned = null;
        applyingComparison = true;
        try
        {
            var rows =
                (ComparisonAfterBox.ItemsSource as IEnumerable<HistoryRow>)
                    ?.Select(row => row.Entry)
                    .ToArray()
                ?? [];
            var period =
                (ComparisonPeriodBox.SelectedItem as PeriodChoice)?.Period
                ?? FolderChangePeriod.Last24Hours;
            ScanHistoryEntry? before;
            ScanHistoryEntry? after;
            ComparisonSelection? selectedRange = null;
            if (period == FolderChangePeriod.Custom)
            {
                before = (ComparisonBeforeBox.SelectedItem as HistoryRow)?.Entry;
                after = (ComparisonAfterBox.SelectedItem as HistoryRow)?.Entry;
            }
            else
            {
                var selection = ComparisonSelection.Resolve(
                    rows,
                    period,
                    DateTimeOffset.UtcNow,
                    TimeZoneInfo.Local
                );
                before = selection.Before;
                after = selection.After;
                selectedRange = selection;
            }
            SelectedBeforeText.Text = before is null
                ? "比較前：記録なし"
                : $"比較前：{before.CompletedLocal:yyyy/MM/dd HH:mm:ss}";
            SelectedAfterText.Text = after is null
                ? "比較後：記録なし"
                : $"比較後：{after.CompletedLocal:yyyy/MM/dd HH:mm:ss}";
            comparisonSelectionKey = (
                period,
                before?.Id,
                after?.Id,
                card.Choice.Drive,
                selectedRange?.IsPartial == true
            );
            if (history is null || before is null || after is null || before.Id == after.Id)
            {
                ComparisonStatusText.Text =
                    "履歴不足：比較前と比較後の異なる2回のスキャンが必要です。サイズ不明を0として差し引きません。";
                await ShowSessionAsync(displayed);
                return;
            }
            if (before.VolumeKey != after.VolumeKey || before.CompletedUtc > after.CompletedUtc)
            {
                ComparisonStatusText.Text =
                    "同じドライブで、比較前が比較後より古いスキャンを選択してください。";
                await ShowSessionAsync(displayed);
                return;
            }
            ComparisonStatusText.Text = "サイズの増減を集計中…";
            var report = await new FolderHistoryService(history).ReadChangesAsync(
                before.VolumeKey,
                before.Id,
                after.Id,
                token
            );
            if (selectedRange is { } range)
                report = report with
                {
                    Period = period,
                    RequestedBaselineUtc = range.RequestedBeforeUtc,
                    AsOfUtc = range.RequestedAfterUtc,
                };
            owned = await ScanSession.OpenHistoryAsync(
                history,
                before.Id,
                card.Choice.Drive,
                token
            );
            token.ThrowIfCancellationRequested();
            if (closed || version != comparisonVersion || selectedCard != card)
                return;
            if (session?.ScanId != after.Id)
                await ChangeHistoryAsync(new(after));
            token.ThrowIfCancellationRequested();
            if (
                closed
                || version != comparisonVersion
                || selectedCard != card
                || session?.ScanId != after.Id
            )
                return;
            comparisonBaseline = owned;
            owned = null;
            comparisonReport = report;
            comparisonFolders = report.Folders.ToDictionary(row => row.Reference);
            ComparisonStatusText.Text =
                (report.IsPartial ? "履歴がある範囲のみ · " : "")
                + (
                    selectedRange is { } requested
                        ? $"指定範囲：{requested.RequestedBeforeUtc.ToLocalTime():yyyy/MM/dd HH:mm} ～ {requested.RequestedAfterUtc.ToLocalTime():yyyy/MM/dd HH:mm} · "
                        : ""
                )
                + $"比較不能 {report.UncertainFolders.Length:N0} フォルダ · 増減は比較後 − 比較前";
            await ShowSessionAsync(session);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!closed && version == comparisonVersion)
            {
                ComparisonStatusText.Text =
                    "比較できません。履歴または容量情報を確認してください。";
                comparisonSelectionKey = null;
                ShowError(error);
            }
        }
        finally
        {
            if (owned is not null)
                await owned.DisposeAsync();
            if (version == comparisonVersion)
                applyingComparison = false;
        }
    }

    private ScanSession ComparisonBrowser(ScanSession current, FileReference folder) =>
        Comparing
        && comparisonBaseline is { } baseline
        && comparisonFolders.GetValueOrDefault(folder)?.Status
            == FolderChangeStatus.NotObservedAfter
            ? baseline
            : current;

    private async Task<ImmutableArray<EntryChange>> ReadComparedEntriesAsync(
        ScanSession current,
        FileReference folder,
        CancellationToken token
    )
    {
        if (cachedComparisonFolder == folder)
            return cachedComparisonEntries;
        var baseline = comparisonBaseline;
        int version = comparisonVersion;
        var change = comparisonFolders.GetValueOrDefault(folder);
        bool root = folder == current.Root;
        var before =
            baseline is not null && (root || change?.BeforePath is not null)
                ? await ReadAllEntriesAsync(baseline, root ? baseline.Root : folder, token)
                : [];
        var after =
            root || change?.AfterPath is not null
                ? await ReadAllEntriesAsync(current, folder, token)
                : [];
        var folders = comparisonFolders;
        var result = await Task.Run(
            () =>
                EntryChanges
                    .Compare(before, after, token)
                    .Select(row =>
                        row.Entry.IsDirectory
                        && folders.GetValueOrDefault(row.Entry.Reference)?.Status
                            == FolderChangeStatus.UncertainCapacity
                            ? row with
                            {
                                CapacityUncertain = true,
                            }
                            : row
                    )
                    .ToImmutableArray(),
            token
        );
        token.ThrowIfCancellationRequested();
        if (version == comparisonVersion && session == current)
        {
            cachedComparisonFolder = folder;
            cachedComparisonEntries = result;
        }
        return result;
    }

    private static async Task<FolderEntry[]> ReadAllEntriesAsync(
        ScanSession source,
        FileReference folder,
        CancellationToken token
    )
    {
        var entries = new List<FolderEntry>();
        for (int page = 0; ; page = checked(page + 1))
        {
            token.ThrowIfCancellationRequested();
            var result = await source.ReadFolderAsync(folder, page, false, token);
            entries.AddRange(result.Entries);
            if (!result.HasMore)
                return entries.ToArray();
        }
    }

    private string? FolderChangeLabel(FileReference reference)
    {
        if (!Comparing)
            return null;
        if (comparisonFolders.TryGetValue(reference, out var change))
            return DisplayText.Change(change.AllocatedDelta, change.Status);
        if (comparisonReport?.Root is { } root && root.Reference == reference)
            return DisplayText.Change(root.AllocatedDelta, root.Status);
        return "比較不能";
    }

    private async Task VerifyComparisonModeAsync()
    {
        if (history is null)
            throw new InvalidOperationException("Comparison smoke requires --demo-history.");
        updatingComparisonChoices = true;
        ComparisonModeBox.SelectedIndex = 1;
        ComparisonPeriodBox.SelectedIndex = 8;
        ComparisonBeforeBox.SelectedIndex = ComparisonBeforeBox.Items.Count - 1;
        ComparisonAfterBox.SelectedIndex = 0;
        updatingComparisonChoices = false;
        await TrackComparisonAsync();
        if (
            comparisonReport is null
            || Entries.Items.Count != 3
            || Entries.Items.Cast<EntryRow>().Any(row => !row.Size.StartsWith('±'))
            || Folders.RootNodes[0].Content is not FolderNode { Change: not null } root
            || !root.Change.StartsWith('±')
        )
            throw new InvalidOperationException(
                "Two retained scans did not show unchanged size deltas."
            );
        var projects = Folders
            .RootNodes[0]
            .Children.Single(node => node.Content is FolderNode { Name: "Projects" });
        int stableVersion = comparisonVersion;
        await RefreshHistoryChoicesAsync(cards[0]);
        if (comparisonVersion != stableVersion)
            throw new InvalidOperationException(
                "Unchanged history refresh restarted the comparison."
            );
        var retainedChoices = ((IEnumerable<HistoryRow>)ComparisonAfterBox.ItemsSource).ToArray();
        UpdateComparisonChoices([retainedChoices[0]]);
        if (ComparisonBeforeBox.SelectedItem is not null)
            throw new InvalidOperationException(
                "A missing custom baseline was silently substituted."
            );
        UpdateComparisonChoices(retainedChoices);
        await LoadFolderAsync(((FolderNode)projects.Content).Location, true);
        if (Entries.Items.Cast<EntryRow>().Any(row => !row.Size.StartsWith('±')))
            throw new InvalidOperationException("Child folder comparison did not load.");
        for (int preset = 0; preset < 8; preset++)
        {
            updatingComparisonChoices = true;
            ComparisonPeriodBox.SelectedIndex = preset;
            updatingComparisonChoices = false;
            await TrackComparisonAsync();
            if (
                comparisonReport
                    is not {
                        Baseline: { } selectedBefore,
                        Latest: { } selectedAfter
                    } selectedReport
                || SelectedBeforeText.Text
                    != $"比較前：{selectedBefore.CompletedLocal:yyyy/MM/dd HH:mm:ss}"
                || SelectedAfterText.Text
                    != $"比較後：{selectedAfter.CompletedLocal:yyyy/MM/dd HH:mm:ss}"
                || ComparisonStatusText.Text.StartsWith("履歴がある範囲のみ")
                    != selectedReport.IsPartial
            )
                throw new InvalidOperationException(
                    $"Preset {preset} did not show selected endpoints and coverage."
                );
        }
        UpdateComparisonChoices([retainedChoices[0]]);
        await TrackComparisonAsync();
        if (comparisonReport is not null || !ComparisonStatusText.Text.StartsWith("履歴不足"))
            throw new InvalidOperationException(
                "One retained snapshot must not produce a zero change."
            );
        UpdateComparisonChoices(retainedChoices);
        updatingComparisonChoices = true;
        ComparisonPeriodBox.SelectedIndex = 8;
        updatingComparisonChoices = false;
        await TrackComparisonAsync();
        var stale = TrackComparisonAsync();
        updatingComparisonChoices = true;
        ComparisonModeBox.SelectedIndex = 0;
        updatingComparisonChoices = false;
        await TrackComparisonAsync();
        await stale;
        if (
            comparisonReport is not null
            || SizeColumnText.Text != "サイズ"
            || Entries.Items.Count != 3
        )
            throw new InvalidOperationException("Cancelled comparison replaced the normal view.");
        if (Environment.GetCommandLineArgs().Contains("--smoke-comparison-image"))
        {
            updatingComparisonChoices = true;
            ComparisonModeBox.SelectedIndex = 1;
            ComparisonPeriodBox.SelectedIndex = 0;
            updatingComparisonChoices = false;
            await TrackComparisonAsync();
        }
    }

    private static IEnumerable<FolderChange> FilterChanges(
        FolderChangeReport report,
        int depth,
        bool leafOnly
    )
    {
        var rows = report.Folders;
        var paths = rows.Where(row => row.AfterPath is not null)
            .Select(row => row.AfterPath!)
            .ToHashSet(StringComparer.Ordinal);
        var parents = paths
            .Where(path => path.Contains('\\'))
            .Select(path => path[..path.LastIndexOf('\\')])
            .ToHashSet(StringComparer.Ordinal);
        return rows.Where(row =>
            row.AfterPath is { } path
            && (depth == 0 || path.Count(c => c == '\\') + 1 == depth)
            && (!leafOnly || !parents.Contains(path))
        );
    }
}
