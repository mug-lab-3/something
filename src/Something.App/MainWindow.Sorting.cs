using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
        VerifyHeaderAlignment(
            Entries,
            EntryNameHeader,
            SizeColumnText,
            "EntryNameText",
            "EntrySizeText"
        );
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

    private void VerifyHeaderAlignment(
        ListView list,
        Button nameHeader,
        Button sizeHeader,
        string namePart,
        string sizePart
    )
    {
        RootLayout.UpdateLayout();
        if (list.Items.Count == 0 || list.ContainerFromIndex(0) is not ListViewItem row)
            throw new InvalidOperationException(
                "Column alignment verification requires a visible row."
            );
        var name = FindTreePart<TextBlock>(row, namePart)!;
        var size = FindTreePart<TextBlock>(row, sizePart)!;
        double labelLeft = nameHeader
            .TransformToVisual(list)
            .TransformPoint(new(nameHeader.Padding.Left, 0))
            .X;
        double bodyLeft = name.TransformToVisual(list).TransformPoint(new()).X;
        double labelRight = sizeHeader
            .TransformToVisual(list)
            .TransformPoint(new(sizeHeader.ActualWidth - sizeHeader.Padding.Right, 0))
            .X;
        double bodyRight = size.TransformToVisual(list).TransformPoint(new(size.ActualWidth, 0)).X;
        if (Math.Abs(labelLeft - bodyLeft) > 1 || Math.Abs(labelRight - bodyRight) > 1)
            throw new InvalidOperationException(
                $"Header/body columns differ: name={labelLeft}/{bodyLeft}; size={labelRight}/{bodyRight}."
            );
        var cells = (Grid)nameHeader.Parent;
        if (
            Math.Abs(nameHeader.ActualHeight - cells.ActualHeight) > 1
            || Math.Abs(sizeHeader.ActualHeight - cells.ActualHeight) > 1
        )
            throw new InvalidOperationException(
                $"Header hover targets do not fill the cells: {nameHeader.ActualHeight}/{sizeHeader.ActualHeight}/{cells.ActualHeight}."
            );
        double nameRight = nameHeader
            .TransformToVisual(cells)
            .TransformPoint(new(nameHeader.ActualWidth, 0))
            .X;
        double sizeLeft = sizeHeader.TransformToVisual(cells).TransformPoint(new()).X;
        if (Math.Abs(nameRight - sizeLeft) > 1)
            throw new InvalidOperationException(
                "Header hover backgrounds have a gap between columns."
            );
        foreach (var header in new[] { nameHeader, sizeHeader })
        {
            foreach (string state in new[] { "PointerOver", "Pressed" })
            {
                if (!VisualStateManager.GoToState(header, state, false))
                    throw new InvalidOperationException("Header pointer state was not applied.");
                RootLayout.UpdateLayout();
                var painted = FindTreePart<ContentPresenter>(header, "HeaderContent")!;
                var origin = painted.TransformToVisual(header).TransformPoint(new());
                if (
                    painted.Background is not SolidColorBrush { Color.A: > 0 }
                    || Math.Abs(origin.X) > 1
                    || Math.Abs(origin.Y) > 1
                    || Math.Abs(painted.ActualWidth - header.ActualWidth) > 1
                    || Math.Abs(painted.ActualHeight - header.ActualHeight) > 1
                    || painted.CornerRadius.TopLeft != 0
                )
                    throw new InvalidOperationException(
                        "Painted header hover background does not fill its column."
                    );
            }
            VisualStateManager.GoToState(header, "Normal", false);
        }
    }
}
