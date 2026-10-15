using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Something.App;

public sealed partial class MainWindow
{
    private int treeScrollVersion;

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
        }
    }

    private void OnTreeSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedNode is { } selected)
            QueueTreeVisibility(selected);
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
