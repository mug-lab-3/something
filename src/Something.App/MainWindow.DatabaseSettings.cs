using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Something.Application;
using Something.Storage;

namespace Something.App;

public sealed partial class MainWindow
{
    private DatabaseSettingsStore? databaseSettings;
    private SettingsDialog? settingsDialog;
    private ContentDialog? databaseDeleteDialog;
    private Task? settingsWork;

    private ScanHistoryArchive CreateConfiguredHistory()
    {
        databaseSettings = new(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Something",
                "database-settings.json"
            )
        );
        return databaseSettings.LoadDirectory() is { } directory
            ? new(directory)
            : ScanHistoryArchive.CreateDefault();
    }

    private async void OnOpenSettings(object sender, RoutedEventArgs args)
    {
        if (settingsDialog is not null || closed)
            return;
        var task = ShowSettingsAsync();
        settingsWork = task;
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
            settingsWork = null;
        }
    }

    private bool CanManageDatabase =>
        scans.Count == 0
        && startupTask.IsCompleted
        && !historyMaintenance
        && !changingVisibility
        && historyTasks.All(task => task == settingsWork || task.IsCompleted)
        && viewUpdates.Count == 0;

    private async Task ShowSettingsAsync()
    {
        while (!closed)
        {
            var dialog = new SettingsDialog(
                visibilityOptions,
                history,
                CanManageDatabase,
                AppWindow.Id
            )
            {
                XamlRoot = RootLayout.XamlRoot,
                RequestedTheme = RootLayout.ActualTheme,
            };
            settingsDialog = dialog;
            ContentDialogResult result;
            try
            {
                result = await dialog.ShowAsync();
            }
            finally
            {
                settingsDialog = null;
            }
            if (closed)
                return;
            if (dialog.DeleteRequested)
            {
                if (await ConfirmDeleteDatabaseAsync())
                    await ManageDatabaseAsync(null);
                continue;
            }
            if (result != ContentDialogResult.Primary)
                return;
            if (
                history is not null
                && dialog.DirectoryPath is { } directory
                && !string.Equals(
                    Path.TrimEndingDirectorySeparator(directory),
                    Path.TrimEndingDirectorySeparator(history.DirectoryPath),
                    StringComparison.OrdinalIgnoreCase
                )
            )
                await ManageDatabaseAsync(directory);
            await ChangeVisibilityAsync(dialog.VisibilityOptions);
            return;
        }
    }

    private async Task<bool> ConfirmDeleteDatabaseAsync()
    {
        if (history is null || !CanManageDatabase)
            throw new InvalidOperationException(
                "スキャンや読み込みが完了してから、データベースを削除してください。"
            );
        databaseDeleteDialog = new()
        {
            Title = "履歴データベースを削除しますか？",
            Content = new TextBlock
            {
                Text =
                    $"全ドライブのスキャン履歴をすべて削除します。この操作は元に戻せません。ディスク上のファイルは削除されません。\n\n{history.DatabasePath}",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
            },
            PrimaryButtonText = "削除",
            CloseButtonText = "キャンセル",
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
        if (history is null || !CanManageDatabase)
            throw new InvalidOperationException(
                "スキャンや読み込みが完了してから、データベースを操作してください。"
            );
        historyMaintenance = true;
        WorkspaceTabs.IsEnabled = SettingsButton.IsEnabled = false;
        foreach (var card in cards)
            card.IsEnabled = false;
        string previousPath = history.DatabasePath;
        bool succeeded = false;
        await using var update = BeginViewUpdate(
            destination is null
                ? "履歴データベースを削除しています…"
                : "履歴データベースを移動しています…",
            destination is null
                ? LoadingAnimationKind.DeleteHistory
                : LoadingAnimationKind.OptimizeDatabase
        );
        try
        {
            await ReleaseDatabaseViewsAsync();
            if (destination is null)
                await Task.Run(() => history.DeleteDatabase(lifetime.Token), lifetime.Token);
            else
                history = await Task.Run(
                    () =>
                        history.Relocate(
                            destination,
                            path => databaseSettings?.SaveDirectory(path),
                            lifetime.Token
                        ),
                    lifetime.Token
                );
            succeeded = true;
        }
        finally
        {
            scanner = new(history);
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
                destination is null ? "全ドライブのスキャン履歴を削除しました。"
                    : File.Exists(previousPath)
                        ? "データベースを新しい保存先に切り替えました。元のデータベースは削除できなかったため、以前のフォルダーに残っています。"
                    : "履歴データベースを新しい保存先に移動しました。",
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
        PathText.Text = "未スキャン";
        BackButton.IsEnabled = ForwardButton.IsEnabled = UpButton.IsEnabled = false;
        EmptyScanText.Visibility = Visibility.Visible;
        updatingHistoryChoices = true;
        HistoryBox.ItemsSource = null;
        HistoryBox.IsEnabled = false;
        updatingHistoryChoices = false;
    }
}
