using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Something.Application;

namespace Something.App;

public sealed partial class MainWindow
{
    private async Task VerifyDatabaseSettingsAsync(string capture)
    {
        string ownedRoot = Path.GetDirectoryName(Path.GetFullPath(capture))!;
        if (
            !demo
            || history is null
            || !string.Equals(
                history.DirectoryPath,
                Path.Combine(ownedRoot, "history"),
                StringComparison.OrdinalIgnoreCase
            )
        )
            throw new InvalidOperationException(
                "Database settings smoke requires its own synthetic history directory."
            );
        await Task.WhenAll(historyTasks.ToArray());
        var ids = cards.ToDictionary(
            card => card.Choice.Drive,
            card => history.List(volumeKeys[card], lifetime.Token).Select(row => row.Id).ToArray()
        );
        string source = history.DatabasePath;
        databaseSettings = new(Path.Combine(ownedRoot, "database-settings.json"));
        WorkspaceTabs.SelectedItem = DatabaseWorkspaceTab;
        await RefreshManagementIfVisibleAsync();
        if (
            history.ReadDatabaseInfo().TotalBytes <= 0
            || ((TextBlock)HistoryManagement.FindName("DatabaseSizeText")).Text.Length == 0
        )
            throw new InvalidOperationException(
                "DB management did not show its physical database size."
            );
        await CaptureStartupFrameAsync(capture + ".database-management");
        SetSettingsOpen(true);
        await Task.Delay(300, lifetime.Token);
        if (
            SettingsPage.DirectoryPath != history.DirectoryPath
            || SettingsPage.VisibilityOptions != visibilityOptions
            || WorkspaceHeader.Visibility != Visibility.Collapsed
            || ViewHost.Visibility != Visibility.Collapsed
        )
            throw new InvalidOperationException(
                "Settings page did not replace the workspace with current preferences."
            );
        await CaptureStartupFrameAsync(capture + ".settings");
        SettingsPage.Search("隠し");
        await Task.Delay(200, lifetime.Token);
        string[] visibleItems =
        [
            .. new[]
            {
                "IncludeSystemItem",
                "IncludeHiddenItem",
                "CurrentDatabaseItem",
                "LocationItem",
                "OpenFolderItem",
                "DeleteDatabaseItem",
            }.Where(name =>
                ((FrameworkElement)SettingsPage.FindName(name)).Visibility == Visibility.Visible
            ),
        ];
        if (visibleItems is not ["IncludeHiddenItem"])
            throw new InvalidOperationException(
                "Settings search did not narrow the items to the matching one: "
                    + string.Join(", ", visibleItems)
            );
        await CaptureStartupFrameAsync(capture + ".settings-search");
        SettingsPage.Search("");
        var hidden = (CheckBox)SettingsPage.FindName("IncludeHidden");
        hidden.IsChecked = false;
        for (
            int wait = 0;
            wait < 50 && (changingVisibility || visibilityOptions.IncludeHidden);
            wait++
        )
            await Task.Delay(100, lifetime.Token);
        if (visibilityOptions.IncludeHidden || hidden.IsChecked != false)
            throw new InvalidOperationException("Settings page did not apply its change at once.");
        hidden.IsChecked = true;
        for (
            int wait = 0;
            wait < 50 && (changingVisibility || !visibilityOptions.IncludeHidden);
            wait++
        )
            await Task.Delay(100, lifetime.Token);
        if (!visibilityOptions.IncludeHidden)
            throw new InvalidOperationException("Settings page did not restore its change.");
        SetSettingsOpen(false);
        if (
            WorkspaceHeader.Visibility != Visibility.Visible
            || HistoryManagement.Visibility != Visibility.Visible
        )
            throw new InvalidOperationException("Closing settings did not restore the workspace.");
        await InvokeMaintenanceAsync("DeleteDatabaseButton", confirm: false);
        if (!File.Exists(source))
            throw new InvalidOperationException("Cancelling DB deletion changed the database.");
        SetSettingsOpen(true);
        ((TextBox)SettingsPage.FindName("DirectoryBox")).Text = Path.Combine(
            ownedRoot,
            "relocated-history"
        );
        await Task.Delay(100, lifetime.Token); // Let TextChanged enable the move action.
        await InvokeMaintenanceAsync("MoveButton", confirm: true);
        if (File.Exists(source) || databaseSettings.LoadDirectory() != history.DirectoryPath)
            throw new InvalidOperationException(
                "DB relocation did not persist its location and remove its source."
            );
        foreach (var card in cards)
            if (
                !ids[card.Choice.Drive]
                    .SequenceEqual(
                        history.List(volumeKeys[card], lifetime.Token).Select(row => row.Id)
                    )
            )
                throw new InvalidOperationException("DB relocation lost a drive's history.");
        await InvokeMaintenanceAsync("DeleteDatabaseButton", confirm: true);
        if (
            history.ReadDatabaseInfo().TotalBytes != 0
            || sessions.Count != 0
            || session is not null
            || Entries.Items.Count != 0
            || HistoryBox.Items.Count != 0
        )
            throw new InvalidOperationException(
                "DB deletion retained history or created a new database prematurely."
            );
        await StartScanAsync(selectedCard!);
        if (
            session is null
            || history.ReadDatabaseInfo().TotalBytes <= 0
            || !File.Exists(history.DatabasePath)
        )
            throw new InvalidOperationException(
                "A scan after DB deletion did not create usable history."
            );
        SetSettingsOpen(true);
        SettingsPage.UpdateAvailability(visibilityOptions, maintainable: false, applying: false);
        if (
            ((Button)SettingsPage.FindName("DeleteDatabaseButton")).IsEnabled
            || ((Button)SettingsPage.FindName("MoveButton")).IsEnabled
            || !((TextBox)SettingsPage.FindName("DirectoryBox")).IsReadOnly
            || ((Button)SettingsPage.FindName("BrowseButton")).IsEnabled
            || ((FrameworkElement)SettingsPage.FindName("BusyHint")).Visibility
                != Visibility.Visible
        )
            throw new InvalidOperationException("Busy settings allowed database mutation.");
        SetSettingsOpen(false);
        await File.WriteAllTextAsync(
            capture + ".database-settings.result",
            "Database size, settings page, search, immediate display settings, cancelled delete confirmation, all-volume relocation, settings persistence, full deletion, empty views, rescan and busy controls passed. Synthetic history only.",
            lifetime.Token
        );

        async Task InvokeMaintenanceAsync(string buttonName, bool confirm)
        {
            await Task.WhenAll(historyTasks.Concat(settingsTasks).ToArray());
            SetSettingsOpen(true);
            var button = (Button)SettingsPage.FindName(buttonName);
            if (!button.IsEnabled)
                throw new InvalidOperationException(
                    $"Settings action is unexpectedly disabled: {buttonName}; idle={DatabaseWorkIdle}; ready={CanManageDatabase}; history-tasks={historyTasks.Count}; settings-tasks={settingsTasks.Count}; target={SettingsPage.DirectoryPath}; source={history.DirectoryPath}."
                );
            Invoke(button);
            for (int wait = 0; wait < 30 && databaseDeleteDialog is null; wait++)
                await Task.Delay(100, lifetime.Token);
            var dialog =
                databaseDeleteDialog
                ?? throw new InvalidOperationException(
                    $"Settings action did not request confirmation: {buttonName}."
                );
            await Task.Delay(150, lifetime.Token);
            var pending = historyTasks.Concat(settingsTasks).ToArray();
            if (pending.Length == 0)
                throw new InvalidOperationException(
                    "Settings action was not tracked while awaiting confirmation."
                );
            await CaptureStartupFrameAsync(
                capture + "." + buttonName + (confirm ? ".confirm" : ".cancel")
            );
            if (confirm)
                Invoke(
                    FindTreePart<Button>(dialog, "PrimaryButton")
                        ?? throw new InvalidOperationException(
                            "Settings confirmation button was not loaded."
                        )
                );
            else
                dialog.Hide();
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token);
            for (
                int wait = 0;
                wait < 30
                    && pending.Any(task =>
                        historyTasks.Contains(task) || settingsTasks.Contains(task)
                    );
                wait++
            )
                await Task.Delay(10, lifetime.Token);
            if (pending.Any(task => historyTasks.Contains(task) || settingsTasks.Contains(task)))
                throw new InvalidOperationException("Settings command tracking did not finish.");
        }

        static void Invoke(Button button)
        {
            var peer = new ButtonAutomationPeer(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        }
    }
}
