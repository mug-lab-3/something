using System.Collections.Immutable;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Something.Application;
using Something.Ntfs;
using Something.Storage;

namespace Something.App;

public sealed partial class MainWindow
{
    private sealed record PeriodChoice(string Label, FolderChangePeriod? Period)
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
    private bool Comparing => ComparisonPeriodBox.SelectedItem is PeriodChoice { Period: not null };
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
        var endpoint = session?.History;
        Guid? after = endpoint?.Id;
        bool partial = false;
        if (period == FolderChangePeriod.Custom)
        {
            before = (ComparisonBeforeBox.SelectedItem as HistoryRow)?.Entry.Id;
        }
        else
        {
            var rows =
                (HistoryBox.ItemsSource as IEnumerable<HistoryRow>)?.Select(row => row.Entry) ?? [];
            var selected = endpoint is null
                ? null
                : ComparisonSelection.ResolveAtScan(rows, period, endpoint, TimeZoneInfo.Local);
            before = selected?.Before?.Id;
            after = selected?.After?.Id;
            partial = selected?.IsPartial == true;
        }
        return comparisonSelectionKey
            != (period, before, after, selectedCard?.Choice.Drive, partial);
    }

    private void InitializeComparisonControls()
    {
        updatingComparisonChoices = true;
        try
        {
            ComparisonPeriodBox.ItemsSource = new PeriodChoice[]
            {
                new(Strings.Get("Period_None"), null),
                new(Strings.Get("Period_24Hours"), FolderChangePeriod.Last24Hours),
                new(Strings.Get("Period_7Days"), FolderChangePeriod.Last7Days),
                new(Strings.Get("Period_30Days"), FolderChangePeriod.Last30Days),
                new(Strings.Get("Period_365Days"), FolderChangePeriod.Last365Days),
                new(Strings.Get("Period_Today"), FolderChangePeriod.Today),
                new(Strings.Get("Period_ThisWeek"), FolderChangePeriod.ThisWeek),
                new(Strings.Get("Period_ThisMonth"), FolderChangePeriod.ThisMonth),
                new(Strings.Get("Period_ThisYear"), FolderChangePeriod.ThisYear),
                new(Strings.Get("Period_Custom"), FolderChangePeriod.Custom),
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
        updatingComparisonChoices = true;
        try
        {
            ComparisonBeforeBox.ItemsSource = rows;
            ComparisonBeforeBox.SelectedItem = beforeId is null
                ? rows.LastOrDefault()
                : rows.FirstOrDefault(row => row.Entry.Id == beforeId);
        }
        finally
        {
            updatingComparisonChoices = false;
        }
    }

    private void OnComparisonPeriodChanged(object sender, SelectionChangedEventArgs args) =>
        OnComparisonOptionsChanged(sender, EventArgs.Empty);

    private async void OnComparisonOptionsChanged(object? sender, EventArgs args)
    {
        if (!updatingComparisonChoices && !closed)
            await TrackComparisonAsync();
    }

    private async Task TrackComparisonAsync()
    {
        await using var viewUpdate = BeginViewUpdate(Strings.Get("Comparison_Applying"));
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
        ComparisonStatusText.Visibility = Comparing ? Visibility.Visible : Visibility.Collapsed;
        comparisonSelectionKey = null;
        ComparisonStatusText.Text = Strings.Get("Comparison_HistoryRequired");
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
        CustomComparisonPanel.Visibility = Comparing ? Visibility.Visible : Visibility.Collapsed;
        var historyHost = Comparing ? CustomHistoryHost : NormalHistoryHost;
        if (!ReferenceEquals(historyHost.Content, HistoryBox))
        {
            NormalHistoryHost.Content = null;
            CustomHistoryHost.Content = null;
            historyHost.Content = HistoryBox;
        }
        NormalHistoryHost.Visibility = Comparing ? Visibility.Collapsed : Visibility.Visible;
        HistoryBox.Header = Comparing
            ? Strings.Get("Comparison_After")
            : Strings.Get("Comparison_Displayed");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(HistoryBox, HistoryBox.Header);
        ToolTipService.SetToolTip(
            HistoryBox,
            Comparing ? Strings.Get("Comparison_AfterTip") : Strings.Get("Comparison_DisplayedTip")
        );
        var period = (ComparisonPeriodBox.SelectedItem as PeriodChoice)?.Period;
        var selection =
            !custom && period is { } selectedPeriod && session?.History is { } endpoint
                ? ComparisonSelection.ResolveAtScan(
                    HistoryBox.Items.Select(row => row.Entry),
                    selectedPeriod,
                    endpoint,
                    TimeZoneInfo.Local
                )
                : null;
        var before = custom ? ComparisonBeforeBox.SelectedItem?.Entry : selection?.Before;
        HistoryBox.SetAutomaticHistory(
            null,
            Comparing && !custom && selection?.After is null,
            selectable: true
        );
        ComparisonBeforeBox.SetAutomaticHistory(
            before is null ? null : new HistoryRow(before),
            Comparing && !custom
        );
        ToolTipService.SetToolTip(
            ComparisonBeforeBox,
            custom
                ? Strings.Get("Comparison_BeforeTip")
                : Strings.Get("Comparison_AutomaticBeforeTip")
        );
        HistoryBox.OtherEndpoint =
            Comparing && before is not null
                ? Strings.Format("Comparison_BeforeEndpoint", before.CompletedLocal)
                : null;
        ComparisonBeforeBox.OtherEndpoint = HistoryBox.DisplayedHistory is { } after
            ? Strings.Format("Comparison_AfterEndpoint", after.Entry.CompletedLocal)
            : Strings.Get("Comparison_AfterMissing");
        ComparisonStatusText.Visibility =
            Comparing && !(comparisonReport is { IsPartial: false, UncertainFolders.Length: 0 })
                ? Visibility.Visible
                : Visibility.Collapsed;
        ChangeHeader.Visibility = RankingChangeHeader.Visibility = Comparing
            ? Visibility.Visible
            : Visibility.Collapsed;
        // Hover cells own half of the 12px row gutters while their label positions stay unchanged.
        ChangeHeaderColumn.Width = RankingChangeColumn.Width = new(Comparing ? 140 + 12 : 0);
        EntryActionsHeaderColumn.Width = AnalysisActionsHeaderColumn.Width = new(
            Comparing ? 54 : 66
        );
        bool updating = updatingComparisonChoices;
        updatingComparisonChoices = true;
        try
        {
            if (!Comparing)
            {
                if (entryOrder.Column == BrowserColumn.Change)
                    entryOrder = new(BrowserColumn.Size, true);
                if (analysisOrder.Column == BrowserColumn.Change)
                    analysisOrder = new(BrowserColumn.Size, true);
                UpdateSortHeaders();
            }
        }
        finally
        {
            updatingComparisonChoices = updating;
        }
        HistoryBox.IsEnabled = HistoryBox.Items.Count > 0;
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
            ComparisonStatusText.Text = Strings.Get("Comparison_HistoryRequired");
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
                (HistoryBox.ItemsSource as IEnumerable<HistoryRow>)
                    ?.Select(row => row.Entry)
                    .ToArray()
                ?? [];
            var period =
                (ComparisonPeriodBox.SelectedItem as PeriodChoice)?.Period
                ?? FolderChangePeriod.Last24Hours;
            ScanHistoryEntry? before;
            ScanHistoryEntry? after = displayed.History;
            ComparisonSelection? selectedRange = null;
            if (period == FolderChangePeriod.Custom)
            {
                before = (ComparisonBeforeBox.SelectedItem as HistoryRow)?.Entry;
            }
            else
            {
                var selection = after is null
                    ? null
                    : ComparisonSelection.ResolveAtScan(rows, period, after, TimeZoneInfo.Local);
                before = selection?.Before;
                after = selection?.After;
                selectedRange = selection;
            }
            ComparisonBeforeBox.SetAutomaticHistory(
                before is null ? null : new HistoryRow(before),
                period != FolderChangePeriod.Custom
            );
            HistoryBox.SetAutomaticHistory(
                null,
                period != FolderChangePeriod.Custom && after is null,
                selectable: true
            );
            HistoryBox.OtherEndpoint = before is null
                ? null
                : Strings.Format("Comparison_BeforeEndpoint", before.CompletedLocal);
            comparisonSelectionKey = (
                period,
                before?.Id,
                after?.Id,
                card.Choice.Drive,
                selectedRange?.IsPartial == true
            );
            if (history is null || before is null || after is null || before.Id == after.Id)
            {
                ComparisonStatusText.Text = Strings.Get("Comparison_NotEnoughHistory");
                await ShowSessionAsync(displayed);
                return;
            }
            // Any two scans of the drive compare in either order: changes are after minus before.
            if (before.VolumeKey != after.VolumeKey)
            {
                ComparisonStatusText.Text = Strings.Get("Comparison_DifferentDrive");
                await ShowSessionAsync(displayed);
                return;
            }
            ComparisonStatusText.Text = Strings.Get("Comparison_Aggregating");
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
            report = await ScanSession.ApplyVisibilityToComparisonAsync(
                report,
                owned,
                displayed,
                visibilityOptions,
                token
            );
            token.ThrowIfCancellationRequested();
            if (closed || version != comparisonVersion || selectedCard != card)
                return;
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
                (report.IsPartial ? Strings.Get("Comparison_PartialPrefix") : "")
                + (
                    selectedRange is { } requested
                        ? Strings.Format(
                            "Comparison_RequestedRangePrefix",
                            requested.RequestedBeforeUtc.ToLocalTime(),
                            requested.RequestedAfterUtc.ToLocalTime()
                        )
                        : ""
                )
                + Strings.Format("Comparison_UncertainFolders", report.UncertainFolders.Length);
            ToolTipService.SetToolTip(ComparisonPeriodBox, ComparisonStatusText.Text);
            ComparisonStatusText.Visibility =
                !report.IsPartial && report.UncertainFolders.Length == 0
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            await ShowSessionAsync(session);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!closed && version == comparisonVersion)
            {
                ComparisonStatusText.Text = Strings.Get("Comparison_Failed");
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

    private FolderChange? GetFolderComparison(FileReference reference)
    {
        if (!Comparing)
            return null;
        if (comparisonFolders.TryGetValue(reference, out var change))
            return change;
        if (comparisonReport?.Root is { } root && root.Reference == reference)
            return root;
        return null;
    }

    // Any two scans compare in either order; one scan against itself shows no change values.
    private async Task VerifyEndpointOrderAsync(HistoryRow[] retained, Guid? restoreBefore)
    {
        async Task CompareAsync(HistoryRow after, HistoryRow before)
        {
            await ChangeHistoryAsync(after);
            updatingComparisonChoices = true;
            ComparisonBeforeBox.SelectedItem = ComparisonBeforeBox.Items.Single(row =>
                row.Entry.Id == before.Entry.Id
            );
            updatingComparisonChoices = false;
            await TrackComparisonAsync();
        }

        await CompareAsync(retained[^1], retained[0]);
        if (
            comparisonReport is not { Baseline: { } swappedBefore, Latest: { } swappedAfter }
            || swappedBefore.Id != retained[0].Entry.Id
            || swappedAfter.Id != retained[^1].Entry.Id
            || Entries.Items.Count == 0
            || Entries.Items.Cast<EntryRow>().Any(row => !row.Delta.StartsWith('±'))
        )
            throw new InvalidOperationException(
                "A newer before scan did not compare with an older after scan."
            );
        await CompareAsync(retained[^1], retained[^1]);
        if (
            comparisonReport is not null
            || Entries.Items.Cast<EntryRow>().Any(row => row.Delta != "—")
        )
            throw new InvalidOperationException(
                "Rows without a comparison claimed that each item could not be compared."
            );
        await CompareAsync(retained[0], retained.Single(row => row.Entry.Id == restoreBefore));
        if (comparisonReport is null)
            throw new InvalidOperationException("The original comparison was not restored.");
    }

    private async Task PreviewCustomComparisonAsync()
    {
        updatingComparisonChoices = true;
        ComparisonPeriodBox.SelectedIndex = 9;
        ComparisonBeforeBox.SelectedIndex = ComparisonBeforeBox.Items.Count - 1;
        updatingComparisonChoices = false;
        await TrackComparisonAsync();
        RootLayout.UpdateLayout();
        await Task.Delay(200, lifetime.Token);
    }

    private async Task VerifyComparisonModeAsync()
    {
        if (history is null)
            throw new InvalidOperationException("Comparison smoke requires --demo-history.");
        await VerifyViewUpdatesAsync();
        updatingComparisonChoices = true;
        ComparisonPeriodBox.SelectedIndex = 9;
        ComparisonBeforeBox.SelectedIndex = ComparisonBeforeBox.Items.Count - 1;
        updatingComparisonChoices = false;
        await TrackComparisonAsync();
        if (
            CustomComparisonPanel.Visibility != Visibility.Visible
            || Math.Abs(
                CustomComparisonPanel.TransformToVisual(ViewControls).TransformPoint(new()).Y
                    - ComparisonPeriodBox.TransformToVisual(ViewControls).TransformPoint(new()).Y
            ) > 4
            || NormalHistoryHost.Visibility != Visibility.Collapsed
            || !ReferenceEquals(CustomHistoryHost.Content, HistoryBox)
            || ComparisonBeforeBox.Header != Strings.Get("Comparison_Before")
            || HistoryBox.Header != Strings.Get("Comparison_After")
        )
            throw new InvalidOperationException(
                "Custom comparison endpoints were not grouped in baseline-to-target order."
            );
        if (
            comparisonReport is null
            || Entries.Items.Count != 3
            || Entries.Items.Cast<EntryRow>().Any(row => !row.Delta.StartsWith('±'))
            || Folders.RootNodes[0].Content is not FolderNode { Change: not null } root
            || !root.Change.StartsWith('±')
        )
            throw new InvalidOperationException(
                "Two retained scans did not show unchanged size deltas."
            );
        if (
            Entries.Items.Cast<EntryRow>().Any(row => row.Size.StartsWith('±') || row.Size == "—")
            || ChangeHeader.Visibility != Visibility.Visible
        )
            throw new InvalidOperationException("Comparison replaced the current size column.");
        await VerifyTreeIndicatorsAsync();
        AnalysisFoldersOnlyBox.IsChecked = false;
        await LoadRankingAsync();
        if (
            Ranking.Items.Cast<RankingRow>().Count(row => !row.IsDirectory) != 4
            || Ranking.Items.Cast<RankingRow>().Any(row => !row.Delta.StartsWith('±'))
        )
            throw new InvalidOperationException(
                "Mixed analysis did not preserve file comparison deltas."
            );
        AnalysisFoldersOnlyBox.IsChecked = true;
        await LoadRankingAsync();
        var projects = Folders
            .RootNodes[0]
            .Children.Single(node => node.Content is FolderNode { Name: "Projects" });
        int stableVersion = comparisonVersion;
        await RefreshHistoryChoicesAsync(cards[0]);
        if (comparisonVersion != stableVersion)
            throw new InvalidOperationException(
                "Unchanged history refresh restarted the comparison."
            );
        var retainedChoices = HistoryBox.Items.ToArray();
        UpdateComparisonChoices([retainedChoices[0]]);
        if (ComparisonBeforeBox.SelectedItem is not null)
            throw new InvalidOperationException(
                "A missing custom baseline was silently substituted."
            );
        UpdateComparisonChoices(retainedChoices);
        await LoadFolderAsync(((FolderNode)projects.Content).Location, true);
        if (Entries.Items.Cast<EntryRow>().Any(row => !row.Delta.StartsWith('±')))
            throw new InvalidOperationException("Child folder comparison did not load.");
        var retainedCustomBaseline = ComparisonBeforeBox.SelectedItem?.Entry.Id;
        await VerifyEndpointOrderAsync(retainedChoices, retainedCustomBaseline);
        for (int preset = 0; preset < 8; preset++)
        {
            updatingComparisonChoices = true;
            ComparisonPeriodBox.SelectedIndex = preset + 1;
            updatingComparisonChoices = false;
            await TrackComparisonAsync();
            if (
                comparisonReport
                    is not {
                        Baseline: { } selectedBefore,
                        Latest: { } selectedAfter
                    } selectedReport
                || ComparisonBeforeBox.DisplayedHistory?.Entry.Id != selectedBefore.Id
                || HistoryBox.DisplayedHistory?.Entry.Id != selectedAfter.Id
                || CustomComparisonPanel.Visibility != Visibility.Visible
                || NormalHistoryHost.Visibility != Visibility.Collapsed
                || ComparisonBeforeBox.SelectedItem?.Entry.Id != retainedCustomBaseline
                || ComparisonStatusText.Text.StartsWith(Strings.Get("Comparison_PartialPrefix"))
                    != selectedReport.IsPartial
            )
                throw new InvalidOperationException(
                    $"Preset {preset} did not show selected endpoints and coverage."
                );
        }
        updatingComparisonChoices = true;
        ComparisonPeriodBox.SelectedIndex = 1;
        updatingComparisonChoices = false;
        await ChangeHistoryAsync(retainedChoices[^1]);
        if (session?.ScanId != retainedChoices[^1].Entry.Id || comparisonReport is not null)
            throw new InvalidOperationException(
                "Preset comparison ignored the selected historical endpoint."
            );
        await ChangeHistoryAsync(retainedChoices[0]);
        if (comparisonReport?.Latest?.Id != retainedChoices[0].Entry.Id)
            throw new InvalidOperationException(
                "Comparison did not follow history navigation back to latest."
            );
        updatingHistoryChoices = true;
        HistoryBox.ItemsSource = new[] { retainedChoices[0] };
        HistoryBox.SelectedItem = retainedChoices[0];
        updatingHistoryChoices = false;
        await TrackComparisonAsync();
        if (
            comparisonReport is not null
            || !ComparisonStatusText.Text.StartsWith(Strings.Get("Comparison_NotEnoughHistory"))
            || ComparisonBeforeBox.DisplayedHistory?.Entry.Id != retainedChoices[0].Entry.Id
            || HistoryBox.DisplayedHistory is not null
            || HistoryBox.SelectedItem?.Entry.Id != retainedChoices[0].Entry.Id
        )
            throw new InvalidOperationException(
                "One retained snapshot must not produce a zero change."
            );
        updatingHistoryChoices = true;
        HistoryBox.ItemsSource = retainedChoices;
        HistoryBox.SelectedItem = retainedChoices.Single(row => row.Entry.Id == session!.ScanId);
        updatingHistoryChoices = false;
        UpdateComparisonChoices(retainedChoices);
        updatingComparisonChoices = true;
        ComparisonPeriodBox.SelectedIndex = 9;
        updatingComparisonChoices = false;
        await TrackComparisonAsync();
        var stale = TrackComparisonAsync();
        updatingComparisonChoices = true;
        ComparisonPeriodBox.SelectedIndex = 0;
        updatingComparisonChoices = false;
        await TrackComparisonAsync();
        await stale;
        if (
            comparisonReport is not null
            || !SizeColumnText
                .Content.ToString()!
                .StartsWith(Strings.Get("Column_Size"), StringComparison.Ordinal)
            || Entries.Items.Count != 3
        )
            throw new InvalidOperationException("Cancelled comparison replaced the normal view.");
        if (Environment.GetCommandLineArgs().Contains("--smoke-comparison-image"))
        {
            updatingComparisonChoices = true;
            ComparisonPeriodBox.SelectedIndex = 1;
            updatingComparisonChoices = false;
            await TrackComparisonAsync();
        }
    }

    private async Task VerifyTreeIndicatorsAsync()
    {
        var parent = Folders.RootNodes[0];
        var location = ((FolderNode)parent.Content).Location;
        System.Numerics.BigInteger?[] deltas = [1, -1, 0, null];
        var nodes = deltas
            .Select(
                (delta, index) =>
                    new TreeViewNode
                    {
                        Content = new FolderNode(
                            Strings.Format("Comparison_PreviewFolder", index),
                            location,
                            Comparing: true,
                            Comparison: new(
                                location.Reference,
                                "before",
                                "after",
                                10,
                                10,
                                delta,
                                delta is null
                                    ? FolderChangeStatus.UncertainCapacity
                                    : FolderChangeStatus.Comparable
                            ),
                            AllocatedBytes: 10
                        ),
                    }
            )
            .ToArray();
        try
        {
            foreach (var node in nodes)
                parent.Children.Add(node);
            RootLayout.UpdateLayout();
            await Task.Delay(100, lifetime.Token);
            foreach (var node in nodes)
            {
                var model = (FolderNode)node.Content;
                var container =
                    Folders.ContainerFromNode(node)
                    ?? throw new InvalidOperationException("Tree row not realized.");
                var name = FindTreePart<TextBlock>(container, "FolderNameText");
                var icon = FindTreePart<FontIcon>(container, "TreeChangeIcon");
                if (
                    name?.Text != model.DisplayName
                    || icon?.Glyph != model.ChangeGlyph
                    || icon?.Foreground is not SolidColorBrush brush
                    || brush.Color != ((SolidColorBrush)model.ChangeBrush).Color
                )
                    throw new InvalidOperationException(
                        "Tree name or colored indicator did not bind."
                    );
                if (model.ToString() != model.DisplayName || model.ChangeWidth.Value != 18)
                    throw new InvalidOperationException(
                        "Tree indicator changed label length or alignment."
                    );
            }
        }
        finally
        {
            foreach (var node in nodes)
                parent.Children.Remove(node);
        }
    }

    private static T? FindTreePart<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        if (parent is T element && element.Name == name)
            return element;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindTreePart<T>(VisualTreeHelper.GetChild(parent, index), name) is { } child)
                return child;
        return null;
    }

    private static IEnumerable<FolderChange> FilterChanges(FolderChangeReport report, bool leafOnly)
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
            row.AfterPath is { } path && (!leafOnly || !parents.Contains(path))
        );
    }
}
