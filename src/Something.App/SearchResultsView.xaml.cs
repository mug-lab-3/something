using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Something.Application;
using Something.Application.Search;
using Something.Ntfs;

namespace Something.App;

/// <summary>A folder of the result in the tree.</summary>
public sealed record SearchTreeEntry(string Name, FileReference Reference)
{
    public string DisplayName => DisplayText.Escape(Name);
}

/// <summary>A context menu being built for a row; the host adds its own items.</summary>
public sealed class SearchMenuRequest(SearchRow row, MenuFlyout menu)
{
    public SearchRow Row { get; } = row;
    public MenuFlyout Menu { get; } = menu;
}

/// <summary>The results screen of the search tab: the conditions as short labels, the counts, the
/// folders of the result as a tree and the entries of the chosen folder as a list. It shows one
/// <see cref="HistorySearch"/> at a time and owns neither its scans nor its lifetime.</summary>
public sealed partial class SearchResultsView : UserControl
{
    private const int PrefetchRows = 30;
    private readonly ObservableCollection<SearchRow> rows = [];
    private readonly List<string> conditionTexts = [];
    private readonly List<Border> chips = [];
    private SearchResultBrowser? browser;
    private string drive = "";
    private int nextPage;
    private bool hasMore;
    private bool loadingMore;
    private bool selectingTree;
    private double chipsWidth = -1;

    public SearchResultsView()
    {
        InitializeComponent();
        Entries.ItemsSource = rows;
        UpdateHeaders();
    }

    /// <summary>The user asked to return to the conditions screen.</summary>
    public event EventHandler? BackToConditionsRequested;

    /// <summary>The user asked for the size history of a row (its button or the menu).</summary>
    public event EventHandler<SearchRow>? GraphRequested;

    /// <summary>The user asked to move a row to the recycle bin.</summary>
    public event EventHandler<SearchRow>? RecycleRequested;

    /// <summary>The user activated a file row (double click).</summary>
    public event EventHandler<SearchRow>? FileActivated;

    /// <summary>A context menu is being opened for a row; the host may add items.</summary>
    public event EventHandler<SearchMenuRequest>? MenuRequested;

    public HistorySearchResult? Result => browser?.Result;
    public string Drive => drive;

    /// <summary>The folder whose entries the list shows, or null without a result.</summary>
    public FileReference? CurrentFolder => browser?.Current;

    /// <summary>The rows loaded so far, for the host to mark files that no longer exist.</summary>
    public IReadOnlyList<SearchRow> Rows => rows;

    /// <summary>Shows a result from the start: the root folder, the name order.</summary>
    public void Show(HistorySearch search, string driveName)
    {
        ArgumentNullException.ThrowIfNull(search);
        drive = driveName;
        browser = new(search.Result);
        ShowSummary(search.Result);
        BuildTree();
        RefreshList();
        UpdateNavigation();
    }

    /// <summary>Forgets the result.</summary>
    public void Clear()
    {
        browser = null;
        rows.Clear();
        Folders.RootNodes.Clear();
        Chips.Children.Clear();
        chips.Clear();
        conditionTexts.Clear();
        CountsText.Text = "";
        PathText.Text = "";
        EmptyText.Visibility = Visibility.Collapsed;
        MoreConditionsButton.Visibility = Visibility.Collapsed;
        UpdateNavigation();
    }

    public bool GoBack() => Navigate(browser?.GoBack() ?? false);

    public bool GoForward() => Navigate(browser?.GoForward() ?? false);

    public bool GoUp() => Navigate(browser?.GoUp() ?? false);

    /// <summary>Opens a folder of the result, as double clicking it does.</summary>
    public void OpenFolder(FileReference folder)
    {
        if (browser is null || !browser.Contains(folder))
            return;
        browser.Open(folder);
        Navigate(true);
    }

    /// <summary>The row of a folder of the result, for the tree's menu and actions.</summary>
    public SearchRow? RowFor(FileReference folder)
    {
        if (browser?.Result.Folder(folder) is not { } item)
            return null;
        return new(item, drive, browser.Result.PathOf(item.Parent));
    }

    private bool Navigate(bool moved)
    {
        if (!moved)
            return false;
        RefreshList();
        SelectInTree(browser!.Current);
        UpdateNavigation();
        return true;
    }

    private void ShowSummary(HistorySearchResult result)
    {
        conditionTexts.Clear();
        conditionTexts.AddRange(
            HistorySearchSummary.Describe(result).Select(SearchSummaryText.Describe)
        );
        chips.Clear();
        foreach (string text in conditionTexts)
            chips.Add(
                new Border
                {
                    CornerRadius = new(12),
                    Padding = new(10, 4, 10, 4),
                    Background = (Microsoft.UI.Xaml.Media.Brush)
                        Microsoft.UI.Xaml.Application.Current.Resources[
                            "SubtleFillColorSecondaryBrush"
                        ],
                    Child = new TextBlock
                    {
                        Text = text,
                        FontSize = 12,
                        TextWrapping = TextWrapping.NoWrap,
                    },
                }
            );
        chipsWidth = -1;
        LayoutChips(ChipsHost.ActualWidth);
        CountsText.Text = Counts(result);
    }

    private static string Counts(HistorySearchResult result)
    {
        if (result.Status == HistorySearchStatus.NoScans)
            return Strings.Get("Search_ResultNoScans");
        if (result.Status == HistorySearchStatus.ChangesNeedTwoScans)
            return Strings.Get("Search_ResultChangesNeedTwoScans");
        var parts = new List<string>
        {
            Strings.Format("Search_Counts", result.MatchedFolders, result.MatchedFiles),
        };
        if (result.UncertainCount > 0)
            parts.Add(Strings.Format("Search_CountsUncertain", result.UncertainCount));
        if (result.UnknownAttributeCount > 0)
            parts.Add(
                Strings.Format("Search_CountsUnknownAttributes", result.UnknownAttributeCount)
            );
        return string.Join(Strings.Get("Search_CountsSeparator"), parts);
    }

    private void OnChipsHostSizeChanged(object sender, SizeChangedEventArgs args) =>
        LayoutChips(args.NewSize.Width);

    // Chips that fit stay in the row; the rest are summarized by a "+N" button that lists every
    // condition. The first chip, the range, is always shown.
    private void LayoutChips(double width)
    {
        if (width <= 0 || Math.Abs(width - chipsWidth) < 1)
            return;
        chipsWidth = width;
        Chips.Children.Clear();
        AllConditions.Children.Clear();
        const double Spacing = 6;
        double reserved = 64;
        double used = 0;
        int shown = 0;
        foreach (var chip in chips)
        {
            chip.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
            double next = used + (shown == 0 ? 0 : Spacing) + chip.DesiredSize.Width;
            bool last = shown == chips.Count - 1;
            if (shown > 0 && next + (last ? 0 : reserved) > width)
                break;
            Chips.Children.Add(chip);
            used = next;
            shown++;
        }
        int hidden = chips.Count - shown;
        MoreConditionsButton.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (hidden > 0)
        {
            MoreConditionsButton.Content = Strings.Format("Search_MoreConditions", hidden);
            foreach (string text in conditionTexts)
                AllConditions.Children.Add(
                    new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }
                );
        }
    }

    private void BuildTree()
    {
        Folders.RootNodes.Clear();
        var root = new TreeViewNode
        {
            Content = new SearchTreeEntry(drive, browser!.Result.Root),
            HasUnrealizedChildren = browser.TreeChildren(browser.Result.Root).Length != 0,
        };
        Folders.RootNodes.Add(root);
        Realize(root);
        root.IsExpanded = true;
        SelectNode(root);
    }

    private void Realize(TreeViewNode node)
    {
        if (!node.HasUnrealizedChildren || browser is null)
            return;
        node.HasUnrealizedChildren = false;
        var parent = ((SearchTreeEntry)node.Content).Reference;
        foreach (var folder in browser.TreeChildren(parent))
            node.Children.Add(
                new TreeViewNode
                {
                    Content = new SearchTreeEntry(folder.Name, folder.Reference),
                    HasUnrealizedChildren = browser.TreeChildren(folder.Reference).Length != 0,
                }
            );
    }

    private void SelectNode(TreeViewNode node)
    {
        selectingTree = true;
        try
        {
            Folders.SelectedNode = node;
        }
        finally
        {
            selectingTree = false;
        }
    }

    // Selects the tree node of a folder, expanding the nodes above it.
    private void SelectInTree(FileReference folder)
    {
        if (browser is null || Folders.RootNodes.Count == 0)
            return;
        var chain = new Stack<FileReference>();
        for (
            FileReference? current = folder;
            current is { } reference && reference != browser.Result.Root;
            current = browser.ParentOf(reference)
        )
            chain.Push(reference);
        var node = Folders.RootNodes[0];
        while (chain.TryPop(out var next))
        {
            Realize(node);
            node.IsExpanded = true;
            var child = node.Children.FirstOrDefault(c =>
                ((SearchTreeEntry)c.Content).Reference == next
            );
            if (child is null)
                break;
            node = child;
        }
        SelectNode(node);
        Folders.UpdateLayout();
    }

    private void OnExpanding(TreeView sender, TreeViewExpandingEventArgs args) =>
        Realize(args.Node);

    private void OnFolderInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (
            selectingTree
            || browser is null
            || args.InvokedItem is not TreeViewNode { Content: SearchTreeEntry entry }
        )
            return;
        OpenFolder(entry.Reference);
    }

    private void RefreshList()
    {
        rows.Clear();
        nextPage = 0;
        hasMore = false;
        if (browser is null)
            return;
        LoadNextPage();
        EmptyText.Text =
            browser.Result.Status != HistorySearchStatus.Completed
                ? ""
                : Strings.Get("Search_ResultEmpty");
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Count != 0)
            Entries.ScrollIntoView(rows[0]);
        UpdateHeaders();
    }

    private void LoadNextPage()
    {
        if (browser is null)
            return;
        var page = browser.Page(nextPage++, out hasMore);
        string parentPath = browser.Result.PathOf(browser.Current);
        foreach (var item in page)
            rows.Add(new SearchRow(item, drive, parentPath));
    }

    private void OnContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args
    )
    {
        if (
            args.InRecycleQueue
            || !hasMore
            || loadingMore
            || args.ItemIndex < rows.Count - PrefetchRows
        )
            return;
        loadingMore = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            LoadNextPage();
            loadingMore = false;
        });
    }

    private void UpdateNavigation()
    {
        BackButton.IsEnabled = browser?.CanGoBack ?? false;
        ForwardButton.IsEnabled = browser?.CanGoForward ?? false;
        UpButton.IsEnabled = browser?.CanGoUp ?? false;
        PathText.Text =
            browser is null ? ""
            : browser.Path.Length == 0 ? $"{drive}\\"
            : $"{drive}\\{DisplayText.Escape(browser.Path)}";
    }

    private void UpdateHeaders()
    {
        var column = browser?.SortColumn ?? SearchColumn.Name;
        bool descending = browser?.Descending ?? false;
        ColumnHeaders.Update(
            NameHeader,
            Strings.Get("Column_Name"),
            column == SearchColumn.Name,
            descending
        );
        ColumnHeaders.Update(
            SizeHeader,
            Strings.Get("Column_Size"),
            column == SearchColumn.Size,
            descending
        );
        ColumnHeaders.Update(
            DeltaHeader,
            Strings.Get("Column_Change"),
            column == SearchColumn.Delta,
            descending
        );
    }

    private void OnSort(object sender, RoutedEventArgs args)
    {
        if (
            browser is null
            || sender is not Button { Tag: string tag }
            || !Enum.TryParse<SearchColumn>(tag, out var column)
        )
            return;
        browser.Sort(column);
        RefreshList();
    }

    private void OnBack(object sender, RoutedEventArgs args) => GoBack();

    private void OnForward(object sender, RoutedEventArgs args) => GoForward();

    private void OnUp(object sender, RoutedEventArgs args) => GoUp();

    private void OnBackToConditions(object sender, RoutedEventArgs args) =>
        BackToConditionsRequested?.Invoke(this, EventArgs.Empty);

    private static SearchRow? RowOf(object? source) =>
        (source as FrameworkElement)?.DataContext as SearchRow;

    private void OnEntryInvoked(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (RowOf(args.OriginalSource) is not { } row)
            return;
        if (row.IsDirectory)
            OpenFolder(row.Reference);
        else
            FileActivated?.Invoke(this, row);
    }

    private void OnGraphEntry(object sender, RoutedEventArgs args)
    {
        if (RowOf(sender) is { } row)
            GraphRequested?.Invoke(this, row);
    }

    private void OnRecycleEntry(object sender, RoutedEventArgs args)
    {
        if (RowOf(sender) is { } row)
            RecycleRequested?.Invoke(this, row);
    }

    private void OnEntryContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (RowOf(args.OriginalSource) is not { } row)
            return;
        Open(row, (FrameworkElement)sender, args);
    }

    private void OnTreeContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (
            (args.OriginalSource as FrameworkElement)?.DataContext
                is not TreeViewNode { Content: SearchTreeEntry entry }
            || entry.Reference == browser?.Result.Root
            || RowFor(entry.Reference) is not { } row
        )
            return;
        Open(row, (FrameworkElement)sender, args);
    }

    private void Open(SearchRow row, FrameworkElement sender, ContextRequestedEventArgs args)
    {
        var menu = new MenuFlyout();
        var graph = new MenuFlyoutItem { Text = Strings.Get("Search_GraphMenuItem") };
        graph.Click += (_, _) => GraphRequested?.Invoke(this, row);
        menu.Items.Add(graph);
        MenuRequested?.Invoke(this, new(row, menu));
        args.Handled = true;
        if (args.TryGetPosition(sender, out var point))
            menu.ShowAt(sender, point);
        else
            menu.ShowAt(sender);
    }
}
