using Microsoft.UI.Xaml;
using Something.Windows;

namespace Something.App;

public sealed partial class MainWindow
{
    private void InitializeSearchActions()
    {
        var results = SearchPanel.Results;
        results.PrepareRow = PrepareSearchRow;
        results.MenuRequested += OnSearchMenuRequested;
        results.FileActivated += async (_, row) => await OpenPathAsync(row.FullPath, false);
        results.RecycleRequested += async (_, row) => await RecycleSearchRowAsync(row);
    }

    // Recycling needs the identity of the volume, which the latest scan of the drive carries. A
    // result of another volume, or of a drive that has not been scanned in this session, offers none.
    private FileRecycleTarget? SearchRecycleTarget(SearchRow row)
    {
        if (
            SearchPanel.Results.Result is not { } result
            || selectedCard is not { } card
            || !sessions.TryGetValue(card.Choice.Drive, out var scan)
            || scan.VolumeKey != result.VolumeKey
        )
            return null;
        return RecycleTarget(
            scan,
            row.FullPath,
            row.Reference,
            row.IsDirectory,
            row.Item.StandardFileAttributes
        );
    }

    private void PrepareSearchRow(SearchRow row)
    {
        var target = SearchRecycleTarget(row);
        row.RecycleVisibility =
            target is null || FileRecyclePolicy.IsProtected(target)
                ? Visibility.Collapsed
                : Visibility.Visible;
        if (target is not null)
            row.Removal = removals.StateFor(target with { StandardFileAttributes = null });
    }

    private async Task RecycleSearchRowAsync(SearchRow row)
    {
        if (SearchRecycleTarget(row) is { } target)
            await TrackRecycleAsync(target, row.Removal);
    }

    private void OnSearchMenuRequested(object? sender, SearchMenuRequest request)
    {
        var row = request.Row;
        AddPathItems(
            request.Menu,
            row.FullPath,
            row.IsDirectory ? null : row.ParentFolderPath,
            row.IsDirectory
        );
    }
}
