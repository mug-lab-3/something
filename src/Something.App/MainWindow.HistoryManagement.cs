using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Something.Application;
using Something.Application.History;
using Something.Windows;

namespace Something.App;

public sealed partial class MainWindow
{
    private bool historyMaintenance;

    private void InitializeHistoryManagement()
    {
        DatabaseWorkspaceTab.IsEnabled = history is not null;
        HistoryManagement.Configure(
            history,
            RefreshAfterHistoryDeletionAsync,
            OptimizeHistoryAsync,
            (message, kind) => BeginViewUpdate(message, kind),
            lifetime.Token
        );
        HistoryManagement.WorkStarted += TrackHistoryManagementWork;
        HistoryManagement.MaintenanceChanged += deleting =>
        {
            historyMaintenance = deleting;
            foreach (var card in cards)
                card.IsEnabled = !deleting && !changingVisibility;
            WorkspaceTabs.IsEnabled = !deleting && !changingVisibility;
            SettingsButton.IsEnabled = !deleting && !changingVisibility && viewUpdates.Count == 0;
            UpdateSettingsAvailability();
        };
    }

    private async Task<HistoryCompactionResult> OptimizeHistoryAsync()
    {
        if (history is null || closed)
            throw new InvalidOperationException(Strings.Get("Database_Unavailable"));
        if (scans.Count != 0 || !startupTask.IsCompleted)
            throw new InvalidOperationException(Strings.Get("Database_OptimizeBusy"));
        await using var update = BeginViewUpdate(
            Strings.Get("Database_Optimizing"),
            LoadingAnimationKind.OptimizeDatabase
        );
        Guid? selectedScan = session?.ScanId;
        var selectedLocation = location;
        int period = ComparisonPeriodBox.SelectedIndex;
        Guid? before = (ComparisonBeforeBox.SelectedItem as HistoryRow)?.Entry.Id;
        await ClearComparisonAsync();
        historyVersion++;
        navigationVersion++;
        rankingVersion++;
        session = null;
        var oldHistorical = historicalSession;
        historicalSession = null;
        var previous = sessions.Values.ToArray();
        sessions.Clear();
        try
        {
            await Task.WhenAll(
                previous
                    .Append(oldHistorical)
                    .OfType<ScanSession>()
                    .Distinct()
                    .Select(current => current.DisposeAsync().AsTask())
            );
            try
            {
                return await history.OptimizeAsync(
                    stage =>
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (!closed)
                                ViewLoadingText.Text = StatusText.Describe(stage);
                        }),
                    lifetime.Token
                );
            }
            catch (InsufficientMemoryException)
            {
                throw new InvalidOperationException(Strings.Get("Database_OptimizeMemory"));
            }
        }
        finally
        {
            if (!closed)
            {
                await InitializeHistoryAsync();
                if (selectedCard is { } card && volumeKeys.TryGetValue(card, out var key))
                {
                    var entries = await history.ListAsync(key, lifetime.Token);
                    if (
                        selectedScan != session?.ScanId
                        && entries.FirstOrDefault(row => row.Id == selectedScan) is { } saved
                    )
                        await ChangeHistoryAsync(new(saved));
                    updatingComparisonChoices = true;
                    ComparisonPeriodBox.SelectedIndex = period;
                    if (before is not null)
                        ComparisonBeforeBox.SelectedItem = ComparisonBeforeBox
                            .Items.Cast<HistoryRow>()
                            .FirstOrDefault(row => row.Entry.Id == before);
                    updatingComparisonChoices = false;
                    UpdateComparisonLayout();
                    if (Comparing)
                        await TrackComparisonAsync();
                    if (session?.ScanId == selectedScan && selectedLocation is not null)
                        await LoadFolderAsync(selectedLocation, false);
                }
            }
        }
    }

    private async void TrackHistoryManagementWork(Task task)
    {
        historyTasks.Add(task);
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!closed)
                ShowError(error);
        }
        finally
        {
            historyTasks.Remove(task);
        }
    }

    private async void OnWorkspaceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (HistoryManagement is null)
            return;
        bool managing = ReferenceEquals(WorkspaceTabs.SelectedItem, DatabaseWorkspaceTab);
        bool analyzing = ReferenceEquals(WorkspaceTabs.SelectedItem, AnalysisWorkspaceTab);
        ApplyWorkspaceVisibility();
        if (managing)
            await RefreshManagementIfVisibleAsync();
        else if (analyzing)
            await LoadRankingAsync();
    }

    // The settings page replaces the selected workspace without changing the selected tab.
    private void ApplyWorkspaceVisibility()
    {
        bool managing = ReferenceEquals(WorkspaceTabs.SelectedItem, DatabaseWorkspaceTab);
        bool analyzing = ReferenceEquals(WorkspaceTabs.SelectedItem, AnalysisWorkspaceTab);
        bool settings = SettingsOpen;
        ViewHost.Visibility = settings || managing ? Visibility.Collapsed : Visibility.Visible;
        HistoryManagement.Visibility =
            !settings && managing ? Visibility.Visible : Visibility.Collapsed;
        FileListPanel.Visibility = analyzing ? Visibility.Collapsed : Visibility.Visible;
        RankingLoadingRegion.Visibility = analyzing ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshManagementIfVisibleAsync()
    {
        if (closed || !ReferenceEquals(WorkspaceTabs.SelectedItem, DatabaseWorkspaceTab))
            return;
        string? key = selectedCard is { } card ? volumeKeys.GetValueOrDefault(card) : null;
        var task = HistoryManagement.LoadAsync(key);
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

    private async Task VerifyHistoryManagementAsync()
    {
        if (!demo || history is null)
            throw new InvalidOperationException("Management smoke requires isolated demo history.");
        await VerifyMaintenanceAnimationsAsync();
        updatingComparisonChoices = true;
        ComparisonPeriodBox.SelectedIndex = 9;
        updatingComparisonChoices = false;
        await TrackComparisonAsync();
        WorkspaceTabs.SelectedItem = DatabaseWorkspaceTab;
        await RefreshManagementIfVisibleAsync();
        if (
            ViewHost.Visibility != Visibility.Collapsed
            || HistoryManagement.Visibility != Visibility.Visible
        )
            throw new InvalidOperationException("Workspace tabs did not replace the file view.");
        await HistoryManagement.VerifySyntheticManagementAsync();
        if (session is not null || Entries.Items.Count != 0 || Comparing)
            throw new InvalidOperationException("Deleted history remained visible or compared.");
        await SelectDriveAsync(cards[1]);
        await RefreshManagementIfVisibleAsync();
        if (
            session?.Drive != cards[1].Choice.Drive
            || !HistoryManagement.IsShowingVolume(volumeKeys[cards[1]])
            || (await history.ListAsync(volumeKeys[cards[1]], lifetime.Token)).Length == 0
        )
            throw new InvalidOperationException("Drive selection did not preserve other history.");
        await Task.WhenAll(SelectDriveAsync(cards[2]), SelectDriveAsync(cards[1]));
        await RefreshManagementIfVisibleAsync();
        if (!HistoryManagement.IsShowingVolume(volumeKeys[cards[1]]))
            throw new InvalidOperationException(
                "An obsolete drive request replaced the history list."
            );
        await StartScanAsync(cards[1]);
        updatingComparisonChoices = true;
        ComparisonPeriodBox.SelectedIndex = 9;
        ComparisonBeforeBox.SelectedItem = HistoryBox.Items.Cast<HistoryRow>().Last();
        updatingComparisonChoices = false;
        await TrackComparisonAsync();
        Guid? retainedBaseline = comparisonBaseline?.ScanId;
        if (retainedBaseline is null)
            throw new InvalidOperationException(
                "Optimization smoke did not open a comparison baseline."
            );
        await HistoryManagement.VerifyStandaloneOptimizationAsync();
        if (
            session?.Drive != cards[1].Choice.Drive
            || !HistoryManagement.IsShowingVolume(volumeKeys[cards[1]])
            || comparisonBaseline?.ScanId != retainedBaseline
        )
            throw new InvalidOperationException(
                "Standalone optimization did not restore the selected history."
            );
        WorkspaceTabs.SelectedIndex = 0;
        if (ViewHost.Visibility != Visibility.Visible || Entries.Items.Count != 3)
            throw new InvalidOperationException("File view was not restored after management.");
        WorkspaceTabs.SelectedItem = DatabaseWorkspaceTab;
        await RefreshManagementIfVisibleAsync();
        await Task.Delay(200, lifetime.Token);
    }

    private async Task RefreshAfterHistoryDeletionAsync(Guid[] deletedIds)
    {
        if (closed || history is null || deletedIds.Length == 0)
            return;
        await using var update = BeginViewUpdate(Strings.Get("HistoryManagement_Refreshing"));
        var deleted = deletedIds.ToHashSet();
        bool viewingDeleted = session is { } viewing && deleted.Contains(viewing.ScanId);
        bool baselineDeleted =
            comparisonBaseline is { } baseline && deleted.Contains(baseline.ScanId);
        if (viewingDeleted || baselineDeleted)
        {
            updatingComparisonChoices = true;
            ComparisonPeriodBox.SelectedIndex = 0;
            updatingComparisonChoices = false;
            await ClearComparisonAsync();
            UpdateComparisonLayout();
        }
        if (closed)
            return;
        viewingDeleted = session is { } currentViewing && deleted.Contains(currentViewing.ScanId);
        historyVersion++;
        if (viewingDeleted)
        {
            listingCancellation?.Cancel();
            navigationVersion++;
            rankingVersion++;
            session = null;
        }
        if (historicalSession is { } historical && deleted.Contains(historical.ScanId))
        {
            historicalSession = null;
            await historical.DisposeAsync();
        }
        foreach (var card in cards)
        {
            string drive = card.Choice.Drive;
            if (sessions.TryGetValue(drive, out var old) && deleted.Contains(old.ScanId))
            {
                sessions.Remove(drive);
                if (!scans.ContainsKey(card))
                    card.SetResult(null);
                await old.DisposeAsync();
            }
            if (closed)
                return;
            if (
                sessions.ContainsKey(drive)
                || scans.ContainsKey(card)
                || !volumeKeys.TryGetValue(card, out var key)
            )
                continue;
            var rows = await history.ListAsync(key, lifetime.Token);
            if (rows.Length == 0)
                continue;
            var next = await history.OpenAsync(rows[0].Id, drive, lifetime.Token);
            if (closed || sessions.ContainsKey(drive) || scans.ContainsKey(card))
                await next.DisposeAsync();
            else
            {
                sessions[drive] = next;
                card.SetResult(next);
            }
        }
        if (closed)
            return;
        if (viewingDeleted && selectedCard is { } selected)
        {
            session = sessions.GetValueOrDefault(selected.Choice.Drive);
            if (session is { } current)
                await ShowSessionAsync(current);
            else
            {
                navigation.Clear();
                forwardHistory.Clear();
                parentLocation = null;
                location = null;
                Folders.RootNodes.Clear();
                Entries.ItemsSource = null;
                Ranking.ItemsSource = null;
                AnalysisCountText.Text = "";
                EntryCountText.DataContext = null;
                PathText.Text = Strings.Get("Drive_NotScanned");
                BackButton.IsEnabled = ForwardButton.IsEnabled = UpButton.IsEnabled = false;
                EmptyScanText.Visibility = Visibility.Visible;
            }
        }
        if (selectedCard is { } currentCard)
            await RefreshHistoryChoicesAsync(currentCard);
    }
}
