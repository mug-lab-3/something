using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Something.Application;

namespace Something.App;

public sealed partial class MainWindow
{
    private BrowserOrder entryOrder = new(BrowserColumn.Size, true);
    private BrowserOrder analysisOrder = new(BrowserColumn.Size, true);

    private void UpdateSortHeaders()
    {
        ColumnHeaders.Update(
            EntryNameHeader,
            "名前",
            entryOrder.Column == BrowserColumn.Name,
            entryOrder.Descending
        );
        ColumnHeaders.Update(
            SizeColumnText,
            "サイズ",
            entryOrder.Column == BrowserColumn.Size,
            entryOrder.Descending
        );
        ColumnHeaders.Update(
            ChangeHeader,
            "増減",
            entryOrder.Column == BrowserColumn.Change,
            entryOrder.Descending
        );
        ColumnHeaders.Update(
            AnalysisNameHeader,
            AnalysisFoldersOnlyBox.IsChecked != false ? "フォルダ" : "名前",
            analysisOrder.Column == BrowserColumn.Name,
            analysisOrder.Descending
        );
        ColumnHeaders.Update(
            AnalysisSizeHeader,
            "サイズ",
            analysisOrder.Column == BrowserColumn.Size,
            analysisOrder.Descending
        );
        ColumnHeaders.Update(
            RankingChangeHeader,
            "増減",
            analysisOrder.Column == BrowserColumn.Change,
            analysisOrder.Descending
        );
    }

    private async void OnEntrySort(object sender, RoutedEventArgs args)
    {
        if (
            sender is not Button { Tag: string tag }
            || !Enum.TryParse<BrowserColumn>(tag, out var column)
        )
            return;
        await SortEntriesAsync(column);
    }

    private async Task SortEntriesAsync(BrowserColumn column)
    {
        entryOrder = entryOrder.Toggle(column);
        UpdateSortHeaders();
        if (location is { } current)
            await LoadFolderAsync(current, false);
    }

    private async void OnAnalysisSort(object sender, RoutedEventArgs args)
    {
        if (
            sender is not Button { Tag: string tag }
            || !Enum.TryParse<BrowserColumn>(tag, out var column)
        )
            return;
        await SortAnalysisAsync(column);
    }

    private async Task SortAnalysisAsync(BrowserColumn column)
    {
        analysisOrder = analysisOrder.Toggle(column);
        UpdateSortHeaders();
        await LoadRankingAsync();
    }

    private async Task VerifyColumnSortingAsync()
    {
        var originalEntryOrder = entryOrder;
        var originalAnalysisOrder = analysisOrder;
        try
        {
            await SortEntriesAsync(BrowserColumn.Name);
            var names = Entries.Items.Cast<EntryRow>().Select(row => row.Entry).ToArray();
            if (
                !names.SequenceEqual(BrowserOrdering.Entries(names, entryOrder))
                || !EntryNameHeader.Content.ToString()!.EndsWith("▲", StringComparison.Ordinal)
            )
                throw new InvalidOperationException("Name header did not sort ascending.");
            await SortEntriesAsync(BrowserColumn.Name);
            names = Entries.Items.Cast<EntryRow>().Select(row => row.Entry).ToArray();
            if (
                !names.SequenceEqual(BrowserOrdering.Entries(names, entryOrder))
                || !EntryNameHeader.Content.ToString()!.EndsWith("▼", StringComparison.Ordinal)
            )
                throw new InvalidOperationException("Name header did not reverse sort order.");
            await SortEntriesAsync(BrowserColumn.Size);
            await SortEntriesAsync(BrowserColumn.Size);
            names = Entries.Items.Cast<EntryRow>().Select(row => row.Entry).ToArray();
            if (!names.SequenceEqual(BrowserOrdering.Entries(names, entryOrder)))
                throw new InvalidOperationException(
                    "Size header did not sort raw capacities ascending."
                );
            await SortAnalysisAsync(BrowserColumn.Name);
            await SortAnalysisAsync(BrowserColumn.Name);
            var paths = Ranking.Items.Cast<RankingRow>().Select(row => row.RelativePath).ToArray();
            if (!paths.SequenceEqual(paths.OrderDescending(StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "Analysis folder header did not reverse order."
                );
            await SortAnalysisAsync(BrowserColumn.Size);
            await SortAnalysisAsync(BrowserColumn.Size);
            if (!AnalysisSizeHeader.Content.ToString()!.EndsWith("▲", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Analysis size header did not show ascending order."
                );
        }
        finally
        {
            entryOrder = originalEntryOrder;
            analysisOrder = originalAnalysisOrder;
            UpdateSortHeaders();
            if (location is { } current)
                await LoadFolderAsync(current, false);
            await LoadRankingAsync();
        }
    }
}
