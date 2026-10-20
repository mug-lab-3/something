using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Something.Application;
using Windows.Foundation;

namespace Something.App;

public sealed partial class MainWindow
{
    private int treeScrollVersion;
    private Task treeReveal = Task.CompletedTask;
    private bool selectionMarkersPinned;
    private (double Offset, int Rows) pinnedMarkers = (double.NaN, -1);
    private FolderLocation? pendingTreeLocation;

    private ListView? TreeList => FindTreePart<ListView>(Folders, "ListControl");

    private void OnTreeLoaded(object sender, RoutedEventArgs args)
    {
        Folders.ApplyTemplate();
        if (TreeList is not { } list)
            return;
        ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Enabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        list.UpdateLayout();
        if (FindListScrollViewer(list) is { } scroll)
        {
            scroll.HorizontalScrollMode = ScrollMode.Enabled;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            if (!selectionMarkersPinned)
            {
                selectionMarkersPinned = true;
                scroll.ViewChanging += (_, change) =>
                    PinSelectionMarkers(change.NextView.HorizontalOffset);
                scroll.ViewChanged += (_, _) => PinSelectionMarkers(scroll.HorizontalOffset);
                // New and recycled rows appear on layout passes rather than on scrolling.
                Folders.LayoutUpdated += (_, _) => PinSelectionMarkers(scroll.HorizontalOffset);
            }
        }
    }

    // The selection marker is part of each row, so it scrolls out of view with long paths.
    // Shift it by the horizontal offset to keep the current folder marked at the left edge.
    private void PinSelectionMarkers(double offset)
    {
        if (closed || TreeList?.ItemsPanelRoot is not Panel rows)
            return;
        if (pinnedMarkers == (offset, rows.Children.Count))
            return;
        pinnedMarkers = (offset, rows.Children.Count);
        foreach (var row in rows.Children)
        {
            if (
                row is not TreeViewItem item
                || FindTreePart<Rectangle>(item, "SelectionIndicator") is not { } marker
            )
                continue;
            if (marker.RenderTransform is not TranslateTransform shift)
                marker.RenderTransform = shift = new TranslateTransform();
            shift.X = offset;
        }
    }

    private async void OnTreeSelectionChanged(
        TreeView sender,
        TreeViewSelectionChangedEventArgs args
    )
    {
        if (sender.SelectedNode is { } selected)
        {
            QueueTreeVisibility(selected);
            if (
                selected.Content is FolderNode { LoadMore: false } folder
                && location != folder.Location
                && pendingTreeLocation != folder.Location
            )
            {
                pendingTreeLocation = folder.Location;
                try
                {
                    await LoadFolderAsync(folder.Location, true);
                }
                finally
                {
                    if (pendingTreeLocation == folder.Location)
                        pendingTreeLocation = null;
                }
            }
        }
    }

    // Shows the current folder in the tree. When following the list, the folders above it are
    // loaded and expanded first; otherwise only a node that is already loaded is selected.
    private void RevealCurrentFolder(FolderLocation target)
    {
        if (!followListInTree)
        {
            SelectLoadedFolder(target);
            return;
        }
        treeReveal = RevealFolderInTreeAsync(target, navigationVersion, session);
    }

    private async Task RevealFolderInTreeAsync(
        FolderLocation target,
        int version,
        ScanSession? current
    )
    {
        // A newer navigation, drive or scan supersedes this reveal.
        bool Current() =>
            !closed && version == navigationVersion && session == current && location == target;
        try
        {
            foreach (var root in Folders.RootNodes)
            {
                if (
                    root.Content is not FolderNode { LoadMore: false } folder
                    || FolderPathSteps.From(folder.Location.Path, target.Path) is not { } steps
                )
                    continue;
                var node = root;
                foreach (string step in steps.Skip(1))
                {
                    var child = await FindTreeChildAsync(node, step);
                    if (!Current())
                        return;
                    if (child is null)
                    {
                        SelectLoadedFolder(target);
                        return;
                    }
                    node.IsExpanded = true;
                    node = child;
                }
                Folders.SelectedNode = node;
                TreeList?.ScrollIntoView(node);
                QueueTreeVisibility(node);
                return;
            }
            SelectLoadedFolder(target);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (Current())
                ShowError(error);
        }
    }

    // Loads a node's children, and further pages, until the folder at the path appears.
    private async Task<TreeViewNode?> FindTreeChildAsync(TreeViewNode parent, string path)
    {
        int loadedPage = -1;
        while (true)
        {
            // Wait for an expansion that is already loading this node instead of skipping it.
            while (treeLoads.ContainsKey(parent))
                await Task.Delay(16, lifetime.Token);
            if (parent.HasUnrealizedChildren)
            {
                if (loadedPage == 0)
                    return null; // The load failed and has already been reported.
                loadedPage = 0;
                await LoadChildrenAsync(parent, 0);
                continue;
            }
            foreach (var child in parent.Children)
                if (
                    child.Content is FolderNode { LoadMore: false } folder
                    && folder.Location.Path == path
                )
                    return child;
            if (
                parent.Children.LastOrDefault()?.Content is FolderNode { LoadMore: true } more
                && more.NextPage > loadedPage
            )
            {
                loadedPage = more.NextPage;
                await LoadChildrenAsync(parent, more.NextPage);
                continue;
            }
            return null;
        }
    }

    private void SelectLoadedFolder(FolderLocation target)
    {
        var pending = new Stack<TreeViewNode>(Folders.RootNodes);
        while (pending.TryPop(out var node))
        {
            if (node.Content is FolderNode { LoadMore: false } folder && folder.Location == target)
            {
                Folders.SelectedNode = node;
                QueueTreeVisibility(node);
                return;
            }
            foreach (var child in node.Children)
                pending.Push(child);
        }
        Folders.SelectedNode = null;
    }

    private void OnTreeSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (Folders.SelectedNode is { } selected)
            QueueTreeVisibility(selected);
    }

    private bool IsCurrentTreeNode(TreeViewNode node)
    {
        var root = node;
        while (true)
        {
            if (Folders.RootNodes.Contains(root))
                return true;
            if (root.Parent is not { } parent)
                return false;
            root = parent;
        }
    }

    private void QueueTreeVisibility(TreeViewNode node)
    {
        if (closed || !IsCurrentTreeNode(node))
            return;
        int version = ++treeScrollVersion;
        void Adjust(int attempt)
        {
            if (closed || version != treeScrollVersion || !IsCurrentTreeNode(node))
                return;
            Folders.UpdateLayout();
            if (TreeList is not { } list || FindListScrollViewer(list) is not { } scroll)
                return;
            if (Folders.ContainerFromNode(node) is not { } container)
            {
                if (attempt == 0)
                {
                    list.ScrollIntoView(node);
                    DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Adjust(1));
                }
                return;
            }
            if (FindTreePart<TextBlock>(container, "FolderNameText") is not { } name)
                return;
            double width = Math.Min(name.ActualWidth, name.DesiredSize.Width);
            var bounds = name.TransformToVisual(scroll)
                .TransformBounds(new Rect(0, 0, width, name.ActualHeight));
            double viewport = scroll.ViewportWidth;
            if (viewport <= 0)
                return;
            // Keep the leading name, change marker and expander visible. A very long
            // label should show its beginning rather than scroll to its last character.
            const double leadingSpace = 44;
            double offset = scroll.HorizontalOffset;
            bool isRoot = Folders.RootNodes.Contains(node);
            double target = isRoot ? 0 : offset;
            if (!isRoot && (bounds.X < leadingSpace || bounds.Right > viewport - 12))
                target = offset + bounds.X - leadingSpace;
            target = Math.Clamp(target, 0, scroll.ScrollableWidth);
            if (Math.Abs(target - offset) > 0.5)
                scroll.ChangeView(target, null, null, true);
        }
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Adjust(0));
    }

    private async Task VerifyTreeHorizontalScrollingAsync()
    {
        var root = Folders.RootNodes[0];
        var previousSelection = Folders.SelectedNode;
        var branch = new TreeViewNode();
        var leaf = branch;
        for (int depth = 0; depth < 14; depth++)
        {
            leaf.Content = new FolderNode(
                $"Level {depth + 1:00}",
                ((FolderNode)root.Content).Location
            );
            if (depth == 13)
                break;
            var child = new TreeViewNode();
            leaf.Children.Add(child);
            leaf.IsExpanded = true;
            leaf = child;
        }
        root.Children.Add(branch);
        try
        {
            RootLayout.UpdateLayout();
            Folders.SelectedNode = leaf;
            TreeList?.ScrollIntoView(leaf);
            QueueTreeVisibility(leaf);
            await Task.Delay(150, lifetime.Token);
            RootLayout.UpdateLayout();
            var scroll =
                FindListScrollViewer(Folders)
                ?? throw new InvalidOperationException("Tree scroll viewer was not found.");
            var container =
                Folders.ContainerFromNode(leaf)
                ?? throw new InvalidOperationException("Deep tree row was not realized.");
            var name = FindTreePart<TextBlock>(container, "FolderNameText")!;
            var bounds = name.TransformToVisual(scroll)
                .TransformBounds(
                    new Rect(
                        0,
                        0,
                        Math.Min(name.ActualWidth, name.DesiredSize.Width),
                        name.ActualHeight
                    )
                );
            if (
                scroll.HorizontalOffset <= 0
                || bounds.X < -1
                || bounds.Right > scroll.ViewportWidth + 1
            )
                throw new InvalidOperationException(
                    $"Deep selection was clipped: offset={scroll.HorizontalOffset}; x={bounds.X}; right={bounds.Right}; viewport={scroll.ViewportWidth}; extent={scroll.ExtentWidth}."
                );
            var marker =
                FindTreePart<Rectangle>(container, "SelectionIndicator")
                ?? throw new InvalidOperationException("Tree selection marker was not found.");
            double markerLeft = marker.TransformToVisual(scroll).TransformPoint(new Point()).X;
            if (marker.Opacity < .99 || markerLeft < -.5 || markerLeft > 12)
                throw new InvalidOperationException(
                    $"The selection marker scrolled out of the visible left edge: x={markerLeft}; offset={scroll.HorizontalOffset}."
                );

            leaf.Content = new FolderNode(
                new string('W', 100),
                ((FolderNode)root.Content).Location
            );
            RootLayout.UpdateLayout();
            QueueTreeVisibility(leaf);
            await Task.Delay(150, lifetime.Token);
            container = Folders.ContainerFromNode(leaf)!;
            name = FindTreePart<TextBlock>(container, "FolderNameText")!;
            double leading = name.TransformToVisual(scroll).TransformPoint(new Point()).X;
            if (leading < -1 || leading > scroll.ViewportWidth - 60)
                throw new InvalidOperationException(
                    "Long tree labels scrolled to their end instead of their beginning."
                );

            QueueTreeVisibility(leaf);
            Folders.SelectedNode = root;
            QueueTreeVisibility(root);
            await Task.Delay(150, lifetime.Token);
            if (scroll.HorizontalOffset > 1)
                throw new InvalidOperationException(
                    "Returning to the root did not restore the left edge."
                );
        }
        finally
        {
            root.Children.Remove(branch);
            Folders.SelectedNode = previousSelection;
            FindListScrollViewer(Folders)?.ChangeView(0, null, null, true);
        }
    }
}
