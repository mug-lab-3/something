using Microsoft.UI.Xaml;
using Something.Storage;

namespace Something.App;

public sealed partial class MainWindow
{
    private bool Analyzing => ReferenceEquals(WorkspaceTabs.SelectedItem, AnalysisWorkspaceTab);

    private async Task VerifyAnalysisWorkspaceAsync()
    {
        var current =
            session ?? throw new InvalidOperationException("Analysis smoke requires a scan.");
        var previousLocation = location!;
        var facts = await current.ReadFolderAnalysisAsync(new(), lifetime.Token);
        var template = facts[0];
        var original = Ranking.ItemsSource;
        var originalOrder = analysisOrder;
        try
        {
            await LoadFolderAsync(new(current.Root, current.Drive + "\\"), false);
            WorkspaceTabs.SelectedItem = AnalysisWorkspaceTab;
            await LoadRankingAsync();
            RootLayout.UpdateLayout();
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
            analysisOrder = originalOrder;
            UpdateSortHeaders();
            Ranking.ItemsSource = original;
            WorkspaceTabs.SelectedIndex = 0;
            await LoadFolderAsync(previousLocation, false);
        }
    }
}
