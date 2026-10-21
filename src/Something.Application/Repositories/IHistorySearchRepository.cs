using System.Collections.Immutable;
using Something.Application.Search;

namespace Something.Application.Repositories;

/// <summary>Searches across the retained scans of one volume. Methods run on the calling thread.</summary>
public interface IHistorySearchRepository
{
    /// <summary>The result keeps the searched scans open until it is disposed.</summary>
    HistorySearchResult Search(
        string volumeKey,
        HistorySearchCriteria criteria,
        ulong memoryLimitBytes,
        DatabaseMemoryGrowth? growth,
        CancellationToken token
    );

    /// <summary>The sizes of one item in every scan of <paramref name="result"/>.</summary>
    ImmutableArray<SearchSizeObservation> ReadSeries(
        HistorySearchResult result,
        SearchItem item,
        CancellationToken token
    );
}
