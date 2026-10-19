using Microsoft.UI.Xaml;
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
        var dialog = new SettingsDialog(visibilityOptions, history, true, AppWindow.Id)
        {
            XamlRoot = RootLayout.XamlRoot,
            RequestedTheme = RootLayout.ActualTheme,
        };
        var showing = dialog.ShowAsync();
        try
        {
            await Task.Delay(300, lifetime.Token);
            if (
                dialog.DirectoryPath != history.DirectoryPath
                || dialog.VisibilityOptions != visibilityOptions
            )
                throw new InvalidOperationException(
                    "Settings dialog did not show current preferences and location."
                );
            await CaptureStartupFrameAsync(capture + ".settings");
        }
        finally
        {
            dialog.Hide();
            await showing;
        }
        var confirmation = ConfirmDeleteDatabaseAsync();
        await Task.Delay(300, lifetime.Token);
        if (databaseDeleteDialog is null)
            throw new InvalidOperationException("DB deletion did not request confirmation.");
        await CaptureStartupFrameAsync(capture + ".database-delete");
        databaseDeleteDialog.Hide();
        if (await confirmation || !File.Exists(source))
            throw new InvalidOperationException("Cancelling DB deletion changed the database.");
        await ManageDatabaseAsync(Path.Combine(ownedRoot, "relocated-history"));
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
        await ManageDatabaseAsync(null);
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
        var blocked = new SettingsDialog(visibilityOptions, history, false, AppWindow.Id);
        if (
            ((Button)blocked.FindName("DeleteDatabaseButton")).IsEnabled
            || !((TextBox)blocked.FindName("DirectoryBox")).IsReadOnly
            || ((Button)blocked.FindName("BrowseButton")).IsEnabled
        )
            throw new InvalidOperationException("Busy settings allowed database mutation.");
        await File.WriteAllTextAsync(
            capture + ".database-settings.result",
            "Database size, settings modal, cancelled delete confirmation, all-volume relocation, settings persistence, full deletion, empty views, rescan and busy controls passed. Synthetic history only.",
            lifetime.Token
        );
    }
}
