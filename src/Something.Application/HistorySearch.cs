using System.Collections.Immutable;
using Something.Application.Repositories;
using Something.Application.Search;

namespace Something.Application;

/// <summary>One search the user ran: its result and the scans it covers, which stay protected
/// from deletion until disposed. Reads run off the calling thread, one at a time.</summary>
public sealed class HistorySearch : IAsyncDisposable
{
    private readonly IHistorySearchSession session;
    private readonly SemaphoreSlim gate = new(1);
    private bool disposed;

    /// <summary>Takes over <paramref name="session"/>; it is released when this is disposed.</summary>
    internal HistorySearch(IHistorySearchSession session) => this.session = session;

    public HistorySearchResult Result => session.Result;

    /// <summary>The sizes of one item of <see cref="Result"/> in every scan, oldest first.</summary>
    public async Task<ImmutableArray<SearchSizeObservation>> ReadSeriesAsync(
        SearchItem item,
        CancellationToken token
    )
    {
        ArgumentNullException.ThrowIfNull(item);
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(() => session.ReadSeries(item, token), token);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed)
                return;
            disposed = true;
            await Task.Run(session.Dispose);
        }
        finally
        {
            gate.Release();
        }
    }
}
