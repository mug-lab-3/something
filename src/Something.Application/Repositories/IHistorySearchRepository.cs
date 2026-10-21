using System.Collections.Immutable;
using Something.Application.Memory;
using Something.Application.Search;

namespace Something.Application.Repositories;

/// <summary>Searches across the retained scans of one volume. Methods run on the calling thread.</summary>
public interface IHistorySearchRepository
{
    /// <summary>Runs a search; the session keeps the searched scans until it is disposed.</summary>
    IHistorySearchSession Search(
        string volumeKey,
        HistorySearchCriteria criteria,
        IMemoryAllowance allowance,
        CancellationToken token
    );
}

/// <summary>One completed search. Reads run on the calling thread; callers do not overlap them.</summary>
public interface IHistorySearchSession : IDisposable
{
    HistorySearchResult Result { get; }

    /// <summary>The sizes of one item of <see cref="Result"/> in every scan, oldest first. A scan
    /// without the item reports <see cref="CapacityObservationState.NotObserved"/>, never zero.</summary>
    ImmutableArray<SearchSizeObservation> ReadSeries(SearchItem item, CancellationToken token);
}
