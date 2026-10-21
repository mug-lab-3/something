using System.Collections.Immutable;
using Something.Storage;

namespace Something.Application;

/// <summary>The retained scans that a range selects, shown while the criteria are edited.</summary>
public sealed record SearchRangePreview(
    int ScanCount,
    ScanHistoryEntry? First,
    ScanHistoryEntry? Last
);

/// <summary>Runs history searches off the UI thread. DuckDB starts with the memory available
/// now and grows through the shared budget, so a search competes fairly with scans of other drives.</summary>
public sealed class HistorySearchService(HistoryService history, ScanMemoryBudget budget)
{
    // The engine's limit covers the sort; this reserves the managed result.
    private const ulong ResultReservation = 64UL << 20;

    public Task<SearchRangePreview> PreviewAsync(
        string volumeKey,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken token
    ) =>
        Task.Run(
            () =>
            {
                var scans = history
                    .Archive.List(volumeKey, token)
                    .Where(scan => scan.CompletedUtc >= startUtc && scan.CompletedUtc <= endUtc)
                    .OrderBy(scan => scan.CompletedUtc)
                    .ThenBy(scan => scan.Id)
                    .ToArray();
                return new SearchRangePreview(
                    scans.Length,
                    scans.FirstOrDefault(),
                    scans.LastOrDefault()
                );
            },
            token
        );

    public async Task<HistorySearchResult> SearchAsync(
        string volumeKey,
        HistorySearchCriteria criteria,
        CancellationToken token
    )
    {
        using var reservation = await budget.ReserveAsync(ResultReservation, token);
        var archive = history.Archive;
        return await Task.Run(
            () =>
                archive.Search(
                    volumeKey,
                    criteria,
                    token,
                    reservation.SizeDuckDatabase(),
                    reservation.TryGrowDuckDatabase,
                    accountMemoryLimit: reservation.AccountDuckDatabaseLimit
                ),
            token
        );
    }

    public Task<ImmutableArray<SearchSizeObservation>> ReadSeriesAsync(
        HistorySearchResult result,
        SearchItem item,
        CancellationToken token
    )
    {
        var archive = history.Archive;
        return Task.Run(() => archive.ReadSearchSeries(result, item, token), token);
    }
}
