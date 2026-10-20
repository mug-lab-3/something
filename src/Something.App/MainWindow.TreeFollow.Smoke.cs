using Microsoft.UI.Xaml.Controls;

namespace Something.App;

public sealed partial class MainWindow
{
    // Moving through folders in the list expands and selects the same folder in the tree.
    private async Task VerifyTreeFollowsListAsync(string capture)
    {
        if (
            !demo
            || session is not { } current
            || Folders.RootNodes.FirstOrDefault() is not { } root
        )
            throw new InvalidOperationException("Tree follow smoke needs a demo scan.");
        WorkspaceTabs.SelectedIndex = 0;
        bool previous = followListInTree;
        var rootLocation = new FolderLocation(current.Root, current.Drive + "\\");
        try
        {
            // Turn following on through the settings page, as a user does.
            SetSettingsOpen(true);
            var box = (CheckBox)SettingsPage.FindName("FollowListInTree");
            box.IsChecked = false;
            box.IsChecked = true;
            SetSettingsOpen(false);
            if (!followListInTree)
                throw new InvalidOperationException("The settings page did not turn following on.");

            await LoadFolderAsync(rootLocation, true);
            await RevealSettledAsync();
            foreach (var child in root.Children)
                child.IsExpanded = false;
            var projects = await ChildLocationAsync(rootLocation, "Projects");
            var archive = await ChildLocationAsync(projects, "Archive");
            // Open each level from the list, as double-clicking a folder row does.
            await LoadFolderAsync(projects, true);
            await LoadFolderAsync(archive, true);
            await RevealSettledAsync();
            var projectsNode = TreeNodeAt(projects);
            var archiveNode = TreeNodeAt(archive);
            Folders.UpdateLayout();
            if (
                archiveNode is null
                || Folders.SelectedNode != archiveNode
                || projectsNode is not { IsExpanded: true }
                || !root.IsExpanded
                || Folders.ContainerFromNode(archiveNode) is null
            )
                throw new InvalidOperationException(
                    "The tree did not expand to and select the folder opened in the list."
                );
            // PrintWindow copies the last composed frame; let the expanded tree render first.
            await Task.Delay(500, lifetime.Token);
            await CaptureStartupFrameAsync(capture + ".tree-follow");

            await NavigateBackAsync();
            await RevealSettledAsync();
            if (location != projects || Folders.SelectedNode != projectsNode)
                throw new InvalidOperationException("The tree did not follow back navigation.");

            // With following off, the list moves without expanding collapsed folders.
            ChangeFollowList(false);
            projectsNode.IsExpanded = false;
            await LoadFolderAsync(archive, true);
            await RevealSettledAsync();
            if (location != archive || projectsNode.IsExpanded)
                throw new InvalidOperationException("The tree expanded while following was off.");

            // Turning following back on reveals the current folder immediately.
            ChangeFollowList(true);
            await RevealSettledAsync();
            if (Folders.SelectedNode != archiveNode || !projectsNode.IsExpanded)
                throw new InvalidOperationException(
                    "Turning following on did not reveal the current folder."
                );
        }
        finally
        {
            ChangeFollowList(previous);
            SettingsPage.SetFollowList(previous);
            await LoadFolderAsync(rootLocation, true);
            await RevealSettledAsync();
        }
        await File.WriteAllTextAsync(
            capture + ".tree-follow.result",
            "Settings toggle, two-level reveal from the list, back navigation, following off and re-enabling passed. Synthetic data only.",
            lifetime.Token
        );

        async Task RevealSettledAsync() =>
            await treeReveal.WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token);

        async Task<FolderLocation> ChildLocationAsync(FolderLocation parent, string name)
        {
            var page = await current.ReadFolderAsync(parent.Reference, 0, true, lifetime.Token);
            var entry =
                page.Entries.FirstOrDefault(row => row.Name == name)
                ?? throw new InvalidOperationException($"Demo folder {name} was not found.");
            return new(entry.Reference, parent.Path.TrimEnd('\\') + "\\" + name);
        }

        TreeViewNode? TreeNodeAt(FolderLocation target)
        {
            var pending = new Stack<TreeViewNode>(Folders.RootNodes);
            while (pending.TryPop(out var node))
            {
                if (
                    node.Content is FolderNode { LoadMore: false } folder
                    && folder.Location == target
                )
                    return node;
                foreach (var child in node.Children)
                    pending.Push(child);
            }
            return null;
        }
    }
}
