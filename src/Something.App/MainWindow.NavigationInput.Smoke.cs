using Microsoft.UI.Xaml.Controls;

namespace Something.App;

public sealed partial class MainWindow
{
    private async Task VerifyNavigationInputAsync()
    {
        if (!demo || session is not { } current)
            throw new InvalidOperationException("Navigation input smoke needs a synthetic scan.");
        WorkspaceTabs.SelectedIndex = 0;
        await LoadFolderAsync(new(current.Root, current.Drive + "\\"), NavigationAction.None);
        var root = location!;
        var projects = Folders
            .RootNodes[0]
            .Children.Single(node => node.Content is FolderNode { Name: "Projects" });
        await LoadFolderAsync(((FolderNode)projects.Content).Location, NavigationAction.New);
        var child = location!;
        if (!await NavigateFromInputAsync(BrowseDirection.Back) || location != root)
            throw new InvalidOperationException("Hardware back did not follow browsing history.");
        if (!await NavigateFromInputAsync(BrowseDirection.Forward) || location != child)
            throw new InvalidOperationException(
                "Hardware forward did not follow browsing history."
            );
        var dialog = new ContentDialog
        {
            XamlRoot = WindowLayout.XamlRoot,
            Title = "Synthetic modal",
            CloseButtonText = "Cancel",
        };
        var showing = dialog.ShowAsync();
        try
        {
            await Task.Delay(150, lifetime.Token);
            if (await NavigateFromInputAsync(BrowseDirection.Back) || location != child)
                throw new InvalidOperationException(
                    "Navigation input moved behind a modal dialog."
                );
        }
        finally
        {
            dialog.Hide();
            await showing;
        }
        SetSettingsOpen(true);
        if (
            !await NavigateFromInputAsync(BrowseDirection.Back)
            || SettingsOpen
            || location != child
        )
            throw new InvalidOperationException(
                "Hardware back did not close settings without changing folders."
            );
        WorkspaceTabs.SelectedItem = DatabaseWorkspaceTab;
        if (await NavigateFromInputAsync(BrowseDirection.Up) || location != child)
            throw new InvalidOperationException(
                "Navigation input moved the hidden file view on the history tab."
            );
        WorkspaceTabs.SelectedIndex = 0;
        if (!await NavigateFromInputAsync(BrowseDirection.Up) || location != root)
            throw new InvalidOperationException("Alt+Up did not navigate to the observed parent.");
        if (await NavigateFromInputAsync(BrowseDirection.Up) || location != root)
            throw new InvalidOperationException(
                "Up navigation should be a no-op at the drive root."
            );
    }
}
