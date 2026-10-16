using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;

namespace Something.App;

/// <summary>Present the complete analysis through small, UI-owned batches of row models.</summary>
public sealed partial class AnalysisRowCollection
    : ObservableCollection<RankingRow>,
        ISupportIncrementalLoading
{
    private const int BatchSize = 250;
    private readonly Func<int, RankingRow> createRow;
    private readonly Func<bool> isCurrent;
    private readonly CancellationToken token;
    private readonly SemaphoreSlim gate = new(1);
    public int TotalCount { get; }

    internal AnalysisRowCollection(
        int totalCount,
        Func<int, RankingRow> createRow,
        Func<bool> isCurrent,
        CancellationToken token
    )
    {
        TotalCount = totalCount;
        this.createRow = createRow;
        this.isCurrent = isCurrent;
        this.token = token;
        AppendBatch();
    }

    public bool HasMoreItems => Count < TotalCount && !token.IsCancellationRequested && isCurrent();

    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count) =>
        AsyncInfo.Run(async requestToken =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                token,
                requestToken
            );
            bool entered = false;
            try
            {
                await gate.WaitAsync(cancellation.Token);
                entered = true;
                await Task.Yield();
                cancellation.Token.ThrowIfCancellationRequested();
                return new LoadMoreItemsResult { Count = HasMoreItems ? (uint)AppendBatch() : 0 };
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return new LoadMoreItemsResult();
            }
            finally
            {
                if (entered)
                    gate.Release();
            }
        });

    private int AppendBatch()
    {
        int before = Count;
        int end = (int)Math.Min((long)TotalCount, (long)Count + BatchSize);
        while (Count < end)
            Add(createRow(Count));
        return Count - before;
    }
}
