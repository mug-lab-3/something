using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Something.Application;

namespace Something.App;

public sealed partial class MainWindow
{
    private void InitializeHistoryManagement()
    {
        DatabaseWorkspaceTab.IsEnabled = history is not null;
        HistoryManagement.Configure(history, RefreshAfterHistoryDeletionAsync, lifetime.Token);
        HistoryManagement.WorkStarted += TrackHistoryManagementWork;
        HistoryManagement.DeletingChanged += deleting =>
        {
            foreach (var card in cards)
                card.IsEnabled = !deleting;
            WorkspaceTabs.IsEnabled = !deleting;
        };
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
        bool managing = WorkspaceTabs.SelectedIndex == 1;
        ViewHost.Visibility = managing ? Visibility.Collapsed : Visibility.Visible;
        HistoryManagement.Visibility = managing ? Visibility.Visible : Visibility.Collapsed;
        if (managing)
            await RefreshManagementIfVisibleAsync();
    }

    private async Task RefreshManagementIfVisibleAsync()
    {
        if (closed || WorkspaceTabs.SelectedIndex != 1)
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
        updatingComparisonChoices = true;
        ComparisonPeriodBox.SelectedIndex = 9;
        updatingComparisonChoices = false;
        await TrackComparisonAsync();
        WorkspaceTabs.SelectedIndex = 1;
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
            || history.List(volumeKeys[cards[1]], lifetime.Token).Length == 0
        )
            throw new InvalidOperationException("Drive selection did not preserve other history.");
        await Task.WhenAll(SelectDriveAsync(cards[2]), SelectDriveAsync(cards[1]));
        await RefreshManagementIfVisibleAsync();
        if (!HistoryManagement.IsShowingVolume(volumeKeys[cards[1]]))
            throw new InvalidOperationException(
                "An obsolete drive request replaced the history list."
            );
        WorkspaceTabs.SelectedIndex = 0;
        if (ViewHost.Visibility != Visibility.Visible || Entries.Items.Count != 3)
            throw new InvalidOperationException("File view was not restored after management.");
        WorkspaceTabs.SelectedIndex = 1;
        await RefreshManagementIfVisibleAsync();
        await Task.Delay(200, lifetime.Token);
    }

    private async Task RefreshAfterHistoryDeletionAsync(Guid[] deletedIds)
    {
        if (closed || history is null || deletedIds.Length == 0)
            return;
        await using var update = BeginViewUpdate("削除後の履歴を反映しています…");
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
            var rows = await Task.Run(() => history.List(key, lifetime.Token), lifetime.Token);
            if (rows.Length == 0)
                continue;
            var next = await ScanSession.OpenHistoryAsync(
                history,
                rows[0].Id,
                drive,
                lifetime.Token
            );
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
                EntryCountText.DataContext = null;
                PathText.Text = "未スキャン";
                BackButton.IsEnabled = ForwardButton.IsEnabled = UpButton.IsEnabled = false;
                StatusText.Text = "保存された履歴はありません。スキャンを開始してください。";
            }
        }
        if (selectedCard is { } currentCard)
            await RefreshHistoryChoicesAsync(currentCard);
    }
}
