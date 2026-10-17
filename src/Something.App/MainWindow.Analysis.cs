using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Something.Application;
using Something.Ntfs;
using Something.Storage;

namespace Something.App;

public sealed partial class MainWindow
{
    private bool Analyzing => ReferenceEquals(WorkspaceTabs.SelectedItem, AnalysisWorkspaceTab);

    private async void OnAnalysisFoldersOnlyChanged(object sender, RoutedEventArgs args)
    {
        if (AnalysisNameHeader is null)
            return;
        UpdateSortHeaders();
        if (session is not null && !closed)
            await LoadRankingAsync();
    }

    private async Task LoadMixedAnalysisAsync(
        ScanSession current,
        FolderLocation? scope,
        int version,
        BrowserOrder order,
        bool leafOnly,
        bool comparing,
        IReadOnlyDictionary<FileReference, uint?> attributes,
        Func<string, bool> inScope
    )
    {
        var token = lifetime.Token;
        var report = comparisonReport;
        var baseline = comparisonBaseline;
        if (comparing && report is null)
        {
            if (Current())
                PublishAnalysis(
                    0,
                    _ => throw new InvalidOperationException("No comparison rows."),
                    version,
                    current
                );
            return;
        }
        var files = await current.ReadFileAnalysisAsync(token);
        if (!Current())
            return;
        CapacityAnalysisItem[] rows;
        if (comparing)
        {
            var oldFiles = baseline is null ? [] : await baseline.ReadFileAnalysisAsync(token);
            if (!Current())
                return;
            // A renamed selected folder has different path prefixes at each endpoint.
            string? beforePath =
                scope is null || scope.Reference == current.Root
                    ? ""
                    : report!
                        .Folders.FirstOrDefault(row => row.Reference == scope.Reference)
                        ?.BeforePath;
            string? beforePrefix =
                beforePath is null ? null
                : beforePath.Length == 0 ? ""
                : beforePath + "\\";
            rows = await Task.Run(
                () =>
                {
                    var changes = EntryChanges.Compare(
                        oldFiles
                            .Where(file =>
                                beforePrefix is not null
                                && file.RelativePath.StartsWith(
                                    beforePrefix,
                                    StringComparison.Ordinal
                                )
                            )
                            .Select(file => file.Entry with { Name = file.RelativePath }),
                        files
                            .Where(file => inScope(file.RelativePath))
                            .Select(file => file.Entry with { Name = file.RelativePath }),
                        token
                    );
                    return BrowserOrdering.Analysis(
                        FilterChanges(report!, leafOnly)
                            .Where(row => inScope(row.AfterPath!))
                            .Select(row =>
                                CapacityAnalysisItem.FromChange(
                                    row,
                                    attributes.GetValueOrDefault(row.Reference)
                                )
                            )
                            .Concat(changes.Select(CapacityAnalysisItem.FromFileChange)),
                        order
                    );
                },
                token
            );
        }
        else
        {
            var folders = await current.ReadFolderAnalysisAsync(new(LeafOnly: leafOnly), token);
            if (!Current())
                return;
            rows = await Task.Run(
                () =>
                    BrowserOrdering.Analysis(
                        folders
                            .Where(folder => inScope(folder.RelativePath))
                            .Select(folder =>
                                CapacityAnalysisItem.FromFolder(
                                    folder,
                                    attributes.GetValueOrDefault(folder.Sample.Reference)
                                )
                            )
                            .Concat(
                                files
                                    .Where(file => inScope(file.RelativePath))
                                    .Select(CapacityAnalysisItem.FromFile)
                            ),
                        order
                    ),
                token
            );
        }
        if (Current())
            PublishAnalysis(
                rows.Length,
                index => new(rows[index], current.Drive),
                version,
                current
            );
        bool Current() =>
            !closed && version == rankingVersion && current == session && scope == location;
    }

    private async Task VerifyAnalysisWorkspaceAsync()
    {
        var current =
            session ?? throw new InvalidOperationException("Analysis smoke requires a scan.");
        var previousLocation = location!;
        var facts = await current.ReadFolderAnalysisAsync(new(), lifetime.Token);
        var template = facts[0];
        var original = Ranking.ItemsSource;
        var originalOrder = analysisOrder;
        bool originalFoldersOnly = AnalysisFoldersOnlyBox.IsChecked != false;
        try
        {
            await LoadFolderAsync(new(current.Root, current.Drive + "\\"), false);
            WorkspaceTabs.SelectedItem = AnalysisWorkspaceTab;
            await LoadRankingAsync();
            RootLayout.UpdateLayout();
            VerifyHeaderAlignment(
                Ranking,
                AnalysisNameHeader,
                AnalysisSizeHeader,
                "AnalysisPathText",
                "AnalysisSizeText"
            );
            if (
                TreePanel.Visibility != Visibility.Visible
                || FileListPanel.Visibility != Visibility.Collapsed
                || RankingLoadingRegion.Visibility != Visibility.Visible
                || ViewHost.Visibility != Visibility.Visible
                || TreeColumn.ActualWidth != 250
                || Ranking.ActualWidth > ResultsContent.ActualWidth - TreeColumn.ActualWidth
                || PathText.ActualHeight <= 0
            )
                throw new InvalidOperationException(
                    "Analysis did not share the tree and navigation layout."
                );

            var projects = Folders
                .RootNodes[0]
                .Children.Single(node => node.Content is FolderNode { Name: "Projects" });
            Folders.SelectedNode = projects;
            for (int attempt = 0; attempt < 500 && pendingTreeLocation is not null; attempt++)
                await Task.Delay(10, lifetime.Token);
            if (
                location?.Reference != ((FolderNode)projects.Content).Location.Reference
                || Ranking.Items.Cast<RankingRow>().SingleOrDefault()?.RelativePath
                    != @"Projects\Archive"
            )
                throw new InvalidOperationException(
                    "Tree selection did not restrict analysis to descendants."
                );
            await NavigateBackAsync();
            if (Ranking.Items.Count != facts.Length || location?.Reference != current.Root)
                throw new InvalidOperationException(
                    "Back navigation did not restore drive-wide analysis."
                );
            await NavigateForwardAsync();
            await SortAnalysisAsync(Something.Application.BrowserColumn.Name);
            if (
                Ranking.Items.Cast<RankingRow>().SingleOrDefault()?.RelativePath
                != @"Projects\Archive"
            )
                throw new InvalidOperationException("Analysis sorting lost its selected scope.");
            await NavigateUpAsync();
            if (location?.Reference != current.Root || Ranking.Items.Count != facts.Length)
                throw new InvalidOperationException(
                    "Analysis parent navigation did not restore its scope."
                );

            AnalysisFoldersOnlyBox.IsChecked = false;
            await LoadRankingAsync();
            var mixed = Ranking.Items.Cast<RankingRow>().ToArray();
            if (
                mixed.Length != facts.Length + 4
                || mixed.Count(row => !row.IsDirectory) != 4
                || !AnalysisNameHeader
                    .Content.ToString()!
                    .StartsWith("名前", StringComparison.Ordinal)
            )
                throw new InvalidOperationException(
                    "Mixed analysis did not include all recursive files."
                );
            var capture = mixed.Single(row => row.RelativePath == @"Media\capture.mp4");
            if (capture.IsDirectory || capture.Size != DisplayText.Bytes((UInt128)4_831_838_208))
                throw new InvalidOperationException(
                    "Analysis used folder type or incorrect capacity for a file."
                );
            RootLayout.UpdateLayout();
            await Task.Delay(100, lifetime.Token);
            if (
                Ranking.ContainerFromItem(capture) is not DependencyObject captureRow
                || FindTreePart<FileAttributeIcon>(captureRow, "AnalysisAttributeIcon")
                    is not { IsDirectory: false }
                || FindTreePart<TextBlock>(captureRow, "AnalysisPathText")?.Text != capture.Path
            )
                throw new InvalidOperationException(
                    "A realized analysis file row retained folder visuals."
                );
            await LoadFolderAsync(((FolderNode)projects.Content).Location, true);
            if (
                Ranking.Items.Count != 3
                || Ranking.Items.Cast<RankingRow>().Count(row => !row.IsDirectory) != 2
            )
                throw new InvalidOperationException(
                    "Mixed analysis lost nested files or escaped the selected scope."
                );
            AnalysisFoldersOnlyBox.IsChecked = true;
            await LoadRankingAsync();
            if (
                Ranking.Items.Count != 1
                || Ranking.Items.Cast<RankingRow>().Any(row => !row.IsDirectory)
            )
                throw new InvalidOperationException(
                    "Folders-only did not restore its original behavior."
                );
            await NavigateUpAsync();

            var rows = new AnalysisRowCollection(
                600,
                index => new(template with { RelativePath = $"folder-{index:D4}" }, current.Drive),
                () => current == session,
                lifetime.Token
            );
            Ranking.ItemsSource = rows;
            for (int attempt = 0; attempt < 10 && rows.Count < 600; attempt++)
            {
                await Task.Delay(150, lifetime.Token);
                Ranking.UpdateLayout();
                var scroll =
                    FindListScrollViewer(Ranking)
                    ?? throw new InvalidOperationException(
                        "Analysis scroll viewer was not loaded."
                    );
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
            }
            if (
                rows.Count != 600
                || rows.HasMoreItems
                || rows.Select(row => row.RelativePath).Distinct().Count() != 600
            )
                throw new InvalidOperationException(
                    "Analysis scrolling truncated or duplicated rows."
                );

            bool valid = true;
            var obsolete = new AnalysisRowCollection(
                600,
                _ => new(template, current.Drive),
                () => valid,
                lifetime.Token
            );
            var pending = obsolete.LoadMoreItemsAsync(250).AsTask();
            valid = false;
            await pending;
            if (obsolete.Count != 250 || obsolete.HasMoreItems)
                throw new InvalidOperationException(
                    "Obsolete analysis appended rows after replacement."
                );

            await OpenAnalysisFolderAsync(new(template, current.Drive));
            if (
                !Analyzing
                || location?.Reference != template.Sample.Reference
                || TreePanel.Visibility != Visibility.Visible
            )
                throw new InvalidOperationException(
                    "Analysis double-click did not retain the analysis workspace."
                );
            if (Ranking.Items.Count != 0)
                throw new InvalidOperationException(
                    "A leaf folder retained out-of-scope analysis."
                );
        }
        finally
        {
            AnalysisFoldersOnlyBox.IsChecked = originalFoldersOnly;
            analysisOrder = originalOrder;
            UpdateSortHeaders();
            Ranking.ItemsSource = original;
            WorkspaceTabs.SelectedIndex = 0;
            await LoadFolderAsync(previousLocation, false);
        }
    }
}
