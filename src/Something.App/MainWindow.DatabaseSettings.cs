using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Something.Application;

namespace Something.App;

public sealed partial class MainWindow
{
    private DatabaseSettingsStore? databaseSettings;
    private ContentDialog? databaseDeleteDialog;

    // Settings actions are awaited at shutdown. Unlike historyTasks, they do not block the
    // database operation they confirm themselves.
    private readonly HashSet<Task> settingsTasks = [];

    private HistoryService CreateConfiguredHistory()
    {
        databaseSettings = new(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Something",
                "database-settings.json"
            )
        );
        return Composition.OpenHistory(databaseSettings.LoadDirectory());
    }

    private void InitializeSettingsPage()
    {
        SettingsPage.Configure(AppWindow.Id);
        SettingsPage.SetTheme(themePreference);
        SettingsPage.ThemeChanged += ChangeTheme;
        SettingsPage.SetLanguage(languagePreference, false);
        SettingsPage.LanguageChanged += ChangeLanguage;
        SettingsPage.RestartRequested += () => _ = RestartForLanguageAsync();
        SettingsPage.SetFollowList(followListInTree);
        SettingsPage.FollowListChanged += ChangeFollowList;
        SettingsPage.SetCheckForUpdates(checkForUpdates);
        SettingsPage.CheckForUpdatesChanged += ChangeCheckForUpdates;
        SettingsPage.CloseRequested += () => SetSettingsOpen(false);
        SettingsPage.VisibilityChanged += options =>
            RunSettingsAction(() => ChangeVisibilityAsync(options));
        SettingsPage.MoveRequested += directory =>
            RunSettingsAction(async () =>
            {
                if (await ConfirmMoveDatabaseAsync(directory))
                {
                    SetSettingsOpen(false);
                    await ManageDatabaseAsync(directory);
                }
            });
        SettingsPage.DeleteRequested += () =>
            RunSettingsAction(async () =>
            {
                if (await ConfirmDeleteDatabaseAsync())
                {
                    SetSettingsOpen(false);
                    await ManageDatabaseAsync(null);
                }
            });
    }

    private bool SettingsOpen => SettingsPage.Visibility == Visibility.Visible;

    private void OnOpenSettings(object sender, RoutedEventArgs args) => SetSettingsOpen(true);

    private void SetSettingsOpen(bool open)
    {
        if (closed || open == SettingsOpen)
            return;
        SettingsPage.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceHeader.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        ApplyWorkspaceVisibility();
        if (open)
        {
            SettingsPage.Present(visibilityOptions, history, CanManageDatabase, changingVisibility);
            SettingsPage.FocusSearch();
        }
        else
            SettingsButton.Focus(FocusState.Programmatic);
    }

    // Background scans and other work can start while the page is open.
    private void UpdateSettingsAvailability()
    {
        if (!closed && SettingsOpen)
            SettingsPage.UpdateAvailability(
                visibilityOptions,
                CanManageDatabase,
                changingVisibility
            );
    }

    private async void RunSettingsAction(Func<Task> action)
    {
        var task = action();
        settingsTasks.Add(task);
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!closed && SettingsOpen)
                SettingsPage.ShowError(error);
            else if (!closed)
                ShowError(error);
        }
        finally
        {
            settingsTasks.Remove(task);
            if (SettingsOpen)
                SettingsPage.Present(
                    visibilityOptions,
                    history,
                    CanManageDatabase,
                    changingVisibility
                );
        }
    }

    private bool CanManageDatabase =>
        DatabaseWorkIdle && settingsTasks.All(task => task.IsCompleted);

    private bool DatabaseWorkIdle =>
        scans.Count == 0
        && startupTask.IsCompleted
        && !historyMaintenance
        && !changingVisibility
        && historyTasks.All(task => task.IsCompleted)
        && viewUpdates.Count == 0;

    private async Task<bool> ConfirmMoveDatabaseAsync(string directory)
    {
        if (history is null || !CanManageDatabase)
            throw new InvalidOperationException(Strings.Get("Database_OperationBusy"));
        return await ConfirmAsync(
            Strings.Get("Database_MoveTitle"),
            Strings.Format("Database_MoveMessage", directory),
            Strings.Get("Database_MoveAction")
        );
    }

    private async Task<bool> ConfirmDeleteDatabaseAsync()
    {
        if (history is null || !CanManageDatabase)
            throw new InvalidOperationException(Strings.Get("Database_DeleteBusy"));
        return await ConfirmAsync(
            Strings.Get("Database_DeleteTitle"),
            Strings.Format("Database_DeleteMessage", history.DatabasePath),
            Strings.Get("Common_Delete")
        );
    }

    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        databaseDeleteDialog = new()
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
            },
            PrimaryButtonText = action,
            CloseButtonText = Strings.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.None,
            PrimaryButtonStyle = (Style)
                Microsoft.UI.Xaml.Application.Current.Resources["ConfirmationDialogButtonStyle"],
            CloseButtonStyle = (Style)
                Microsoft.UI.Xaml.Application.Current.Resources["SecondaryDialogButtonStyle"],
            XamlRoot = RootLayout.XamlRoot,
            RequestedTheme = RootLayout.ActualTheme,
        };
        try
        {
            return await databaseDeleteDialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            databaseDeleteDialog = null;
        }
    }

    private async Task ManageDatabaseAsync(string? destination)
    {
        if (history is null || !DatabaseWorkIdle)
            throw new InvalidOperationException(Strings.Get("Database_OperationBusy"));
        historyMaintenance = true;
        WorkspaceTabs.IsEnabled = SettingsButton.IsEnabled = false;
        foreach (var card in cards)
            card.IsEnabled = false;
        string previousPath = history.DatabasePath;
        bool succeeded = false;
        await using var update = BeginViewUpdate(
            destination is null ? Strings.Get("Database_Deleting") : Strings.Get("Database_Moving"),
            destination is null
                ? LoadingAnimationKind.DeleteHistory
                : LoadingAnimationKind.OptimizeDatabase
        );
        try
        {
            await ReleaseDatabaseViewsAsync();
            if (destination is null)
                await history.DeleteDatabaseAsync(lifetime.Token);
            else
                await history.MoveAsync(
                    destination,
                    path => databaseSettings?.SaveDirectory(path),
                    lifetime.Token
                );
            succeeded = true;
        }
        finally
        {
            HistoryManagement.Configure(
                history,
                RefreshAfterHistoryDeletionAsync,
                OptimizeHistoryAsync,
                (message, kind) => BeginViewUpdate(message, kind),
                lifetime.Token
            );
            try
            {
                if (!closed)
                {
                    await InitializeHistoryAsync();
                    await RefreshManagementIfVisibleAsync();
                }
            }
            finally
            {
                historyMaintenance = false;
                if (!closed)
                {
                    WorkspaceTabs.IsEnabled = true;
                    foreach (var card in cards)
                        card.IsEnabled = true;
                }
            }
        }
        if (succeeded && !closed)
            ShowToast(
                destination is null ? Strings.Get("Database_Deleted")
                    : File.Exists(previousPath) ? Strings.Get("Database_MovedOriginalKept")
                    : Strings.Get("Database_Moved"),
                destination is not null && File.Exists(previousPath)
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success
            );
    }

    private async Task ReleaseDatabaseViewsAsync()
    {
        listingCancellation?.Cancel();
        historyVersion++;
        navigationVersion++;
        rankingVersion++;
        await ClearComparisonAsync();
        updatingComparisonChoices = true;
        ComparisonPeriodBox.SelectedIndex = 0;
        updatingComparisonChoices = false;
        UpdateComparisonLayout();
        var previous = sessions
            .Values.Append(historicalSession)
            .OfType<ScanSession>()
            .Distinct()
            .ToArray();
        historicalSession = null;
        session = null;
        sessions.Clear();
        await Task.WhenAll(previous.Select(current => current.DisposeAsync().AsTask()));
        foreach (var card in cards)
            card.SetResult(null);
        navigation.Clear();
        forwardHistory.Clear();
        location = parentLocation = null;
        Folders.RootNodes.Clear();
        Entries.ItemsSource = Ranking.ItemsSource = null;
        AnalysisCountText.Text = "";
        EntryCountText.DataContext = null;
        PathText.Text = Strings.Get("Drive_NotScanned");
        BackButton.IsEnabled = ForwardButton.IsEnabled = UpButton.IsEnabled = false;
        EmptyScanText.Visibility = Visibility.Visible;
        updatingHistoryChoices = true;
        HistoryBox.ItemsSource = null;
        HistoryBox.IsEnabled = false;
        updatingHistoryChoices = false;
    }
}
