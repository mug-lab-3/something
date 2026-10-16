using Microsoft.UI.Xaml;
using Something.Storage;

namespace Something.App;

public sealed partial class MainWindow
{
    private async Task VerifyAnalysisWorkspaceAsync()
    {
        var current =
            session ?? throw new InvalidOperationException("Analysis smoke requires a scan.");
        var previousLocation = location!;
        var facts = await current.ReadFolderAnalysisAsync(new(), lifetime.Token);
        var template = facts[0];
        var original = Ranking.ItemsSource;
        try
        {
            WorkspaceTabs.SelectedItem = AnalysisWorkspaceTab;
            RootLayout.UpdateLayout();
            if (
                TreePanel.Visibility != Visibility.Collapsed
                || FileListPanel.Visibility != Visibility.Collapsed
                || RankingLoadingRegion.Visibility != Visibility.Visible
                || ViewHost.Visibility != Visibility.Visible
                || TreeColumn.ActualWidth != 0
                || Ranking.ActualWidth < ResultsContent.ActualWidth - 40
            )
                throw new InvalidOperationException(
                    "Analysis did not use the full workspace width."
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
                WorkspaceTabs.SelectedIndex != 0
                || location?.Reference != template.Sample.Reference
                || TreePanel.Visibility != Visibility.Visible
            )
                throw new InvalidOperationException(
                    "Analysis did not navigate to the file workspace."
                );
        }
        finally
        {
            Ranking.ItemsSource = original;
            WorkspaceTabs.SelectedIndex = 0;
            await LoadFolderAsync(previousLocation, false);
        }
    }
}
