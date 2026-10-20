using System.Diagnostics;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Something.Statistics;
using Something.Storage;

namespace Something.App;

/// <summary>
/// Settings page: a section tree, searchable setting items and immediate application. Database
/// relocation and deletion are explicit actions that the main window confirms and runs.
/// </summary>
public sealed partial class SettingsView : UserControl
{
    private readonly Section[] sections;
    private WindowId window;
    private string? currentDirectory;
    private bool canMaintain;
    private bool updating;
    private bool scrollingToSection;

    internal event Action? CloseRequested;
    internal event Action<FileVisibilityOptions>? VisibilityChanged;
    internal event Action<string>? MoveRequested;
    internal event Action? DeleteRequested;

    internal FileVisibilityOptions VisibilityOptions =>
        new(IncludeSystem.IsChecked == true, IncludeHidden.IsChecked == true);

    internal string? DirectoryPath => currentDirectory is null ? null : DirectoryBox.Text.Trim();

    public SettingsView()
    {
        InitializeComponent();
        var display = new Section(DisplayHeader, null, []);
        var database = new Section(DatabaseHeader, null, []);
        sections =
        [
            display,
            new(FilesHeader, display, [IncludeSystemItem, IncludeHiddenItem]),
            database,
            new(LocationHeader, database, [CurrentDatabaseItem, LocationItem]),
            new(ManagementHeader, database, [OpenFolderItem, DeleteDatabaseItem]),
        ];
        ApplySearch();
    }

    internal void Configure(WindowId owner) => window = owner;

    /// <summary>Shows current preferences and, when given, reloads the database location.</summary>
    internal void Present(
        FileVisibilityOptions options,
        ScanHistoryArchive? archive,
        bool maintainable,
        bool applying
    )
    {
        string? directory = archive?.DirectoryPath;
        if (!string.Equals(directory, currentDirectory, StringComparison.OrdinalIgnoreCase))
        {
            currentDirectory = directory;
            DirectoryBox.Text = directory ?? "";
        }
        if (archive is not null)
        {
            var info = archive.ReadDatabaseInfo();
            SizeText.Text = DisplayText.Bytes((UInt128)info.TotalBytes);
            CurrentPathText.Text = info.Path;
        }
        UpdateAvailability(options, maintainable, applying);
        ApplySearch();
    }

    /// <summary>Follows background work without reloading the database location.</summary>
    internal void UpdateAvailability(
        FileVisibilityOptions options,
        bool maintainable,
        bool applying
    )
    {
        // While a change applies, the boxes already show the requested state.
        if (!applying)
        {
            updating = true;
            IncludeSystem.IsChecked = options.IncludeSystem;
            IncludeHidden.IsChecked = options.IncludeHidden;
            updating = false;
        }
        IncludeSystem.IsEnabled = IncludeHidden.IsEnabled = !applying;
        canMaintain = maintainable && currentDirectory is not null;
        DirectoryBox.IsReadOnly = !canMaintain;
        BrowseButton.IsEnabled = DeleteDatabaseButton.IsEnabled = canMaintain;
        OpenFolderButton.IsEnabled = currentDirectory is not null;
        BusyHint.Visibility =
            canMaintain || DatabaseHeader.Visibility != Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        UpdateMoveButton();
    }

    internal void FocusSearch() => SearchBox.Focus(FocusState.Programmatic);

    internal void ShowError(Exception error)
    {
        ErrorBar.Message = StatusText.Describe(error);
        ErrorBar.IsOpen = true;
    }

    internal void Search(string query)
    {
        SearchBox.Text = query;
        ApplySearch();
    }

    private void OnBack(object sender, RoutedEventArgs args) => CloseRequested?.Invoke();

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        ApplySearch();

    private void OnVisibilityChanged(object sender, RoutedEventArgs args)
    {
        if (!updating)
            VisibilityChanged?.Invoke(VisibilityOptions);
    }

    private void OnDirectoryChanged(object sender, TextChangedEventArgs args) => UpdateMoveButton();

    private void UpdateMoveButton() =>
        MoveButton.IsEnabled =
            canMaintain
            && DirectoryPath is { Length: > 0 } target
            && !string.Equals(
                Path.TrimEndingDirectorySeparator(target),
                Path.TrimEndingDirectorySeparator(currentDirectory!),
                StringComparison.OrdinalIgnoreCase
            );

    private void OnMove(object sender, RoutedEventArgs args)
    {
        ErrorBar.IsOpen = false;
        try
        {
            string target = DirectoryPath ?? "";
            if (!Path.IsPathFullyQualified(target))
                throw new ArgumentException(Strings.Get("Settings_AbsolutePathRequired"));
            MoveRequested?.Invoke(Path.GetFullPath(target));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            ShowError(error);
        }
    }

    private async void OnBrowse(object sender, RoutedEventArgs args)
    {
        if (DirectoryBox.IsReadOnly)
            return;
        try
        {
            var result = await new FolderPicker(window).PickSingleFolderAsync();
            if (result is not null)
                DirectoryBox.Text = result.Path;
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs args)
    {
        try
        {
            if (currentDirectory is null)
                return;
            Directory.CreateDirectory(currentDirectory);
            Process.Start(
                new ProcessStartInfo { FileName = currentDirectory, UseShellExecute = true }
            );
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void OnDeleteDatabase(object sender, RoutedEventArgs args) => DeleteRequested?.Invoke();

    // Items stay visible when the query matches their own text or a heading above them.
    private void ApplySearch()
    {
        string query = SearchBox.Text.Trim();
        bool hasDatabase = currentDirectory is not null;
        int matches = 0;
        foreach (var section in sections)
        {
            if (section.Parent is not { } parent)
                continue;
            bool headingMatches =
                Matches(TextOf(section.Header), query) || Matches(TextOf(parent.Header), query);
            bool available = parent.Header != DatabaseHeader || hasDatabase;
            bool any = false;
            foreach (var item in section.Items)
            {
                bool visible = available && (headingMatches || Matches(TextOf(item), query));
                item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                any |= visible;
                matches += visible ? 1 : 0;
            }
            section.Header.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var section in sections.Where(section => section.Parent is null))
            section.Header.Visibility = sections.Any(child =>
                child.Parent == section && child.Header.Visibility == Visibility.Visible
            )
                ? Visibility.Visible
                : Visibility.Collapsed;
        BusyHint.Visibility =
            !canMaintain && DatabaseHeader.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;
        NoResultsText.Text = Strings.Format("Settings_NoResults", query);
        NoResultsText.Visibility = matches == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildTree();
    }

    private void RebuildTree()
    {
        SectionTree.RootNodes.Clear();
        foreach (
            var section in sections.Where(section =>
                section.Parent is null && section.Header.Visibility == Visibility.Visible
            )
        )
        {
            var node = new TreeViewNode { Content = new SectionChoice(section), IsExpanded = true };
            foreach (
                var child in sections.Where(child =>
                    child.Parent == section && child.Header.Visibility == Visibility.Visible
                )
            )
                node.Children.Add(new TreeViewNode { Content = new SectionChoice(child) });
            SectionTree.RootNodes.Add(node);
        }
    }

    private void OnSectionInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode { Content: SectionChoice choice })
            return;
        scrollingToSection = true;
        double top = choice.Section.Header.TransformToVisual(ItemPanel).TransformPoint(new(0, 0)).Y;
        ItemScroll.ChangeView(null, Math.Max(0, top - ItemPanel.Padding.Top), null);
    }

    // Select the last heading that has scrolled to the top.
    private void OnItemsScrolled(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (args.IsIntermediate)
            return;
        if (scrollingToSection)
        {
            scrollingToSection = false;
            return;
        }
        var nodes = SectionTree.RootNodes.SelectMany(node => node.Children.Prepend(node)).ToArray();
        TreeViewNode? current = null;
        foreach (var node in nodes)
        {
            var header = ((SectionChoice)node.Content).Section.Header;
            double top = header.TransformToVisual(ItemPanel).TransformPoint(new(0, 0)).Y;
            if (top <= ItemScroll.VerticalOffset + 1)
                current = node;
        }
        SectionTree.SelectedNode = current ?? nodes.FirstOrDefault();
    }

    private static bool Matches(string text, string query) =>
        query.Length == 0
        || CultureInfo.CurrentCulture.CompareInfo.IndexOf(
            text,
            query,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType
        ) >= 0;

    // Search reads the localized text that the item actually displays.
    private static string TextOf(DependencyObject element) =>
        element switch
        {
            TextBlock text => text.Text,
            ContentControl { Content: string content } => content,
            ContentControl { Content: DependencyObject child } => TextOf(child),
            TextBox box => box.PlaceholderText,
            Panel panel => string.Join('\n', panel.Children.Select(TextOf)),
            Border { Child: { } child } => TextOf(child),
            _ => "",
        };

    private sealed record Section(TextBlock Header, Section? Parent, FrameworkElement[] Items);

    private sealed record SectionChoice(Section Section)
    {
        public override string ToString() => Section.Header.Text;
    }
}
