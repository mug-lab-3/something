using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Data;
using Something.Storage;
using Windows.Foundation;

namespace Something.App;

/// <summary>UI-owned rows. ListView requests further database batches near the end of the scroll range.</summary>
public sealed partial class FolderEntryCollection
    : ObservableCollection<EntryRow>,
        ISupportIncrementalLoading
{
    private readonly Func<int, CancellationToken, Task<FolderEntryPage>> readPage;
    private readonly Action<Exception> onError;
    private readonly CancellationToken navigationToken;
    private readonly SemaphoreSlim gate = new(1);
    private readonly long totalCount;
    private int nextPage = 1;
    private bool hasMore;
    private readonly Func<FolderEntry, EntryRow> createRow;

    internal FolderEntryCollection(
        FolderEntryPage first,
        Func<int, CancellationToken, Task<FolderEntryPage>> readPage,
        Action<Exception> onError,
        CancellationToken navigationToken,
        Func<FolderEntry, EntryRow>? createRow = null
    )
    {
        this.readPage = readPage;
        this.onError = onError;
        this.navigationToken = navigationToken;
        this.createRow = createRow ?? (entry => new(entry));
        totalCount =
            first.TotalCount
            ?? throw new ArgumentException("A total entry count is required.", nameof(first));
        hasMore = first.HasMore;
        foreach (var entry in first.Entries)
            Add(this.createRow(entry));
    }

    public bool HasMoreItems => hasMore && !navigationToken.IsCancellationRequested;
    public string TotalCountText => Strings.Format("Count_Items", totalCount);
    public string CountDescription =>
        Count < totalCount
            ? Strings.Format("Count_ItemsLoaded", totalCount, Count)
            : Strings.Format("Count_Items", totalCount);

    /// <summary>All rows of the folder for export, reading unloaded pages without showing them.</summary>
    internal async Task<IReadOnlyList<EntryRow>> ReadAllAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var rows = new List<EntryRow>(this);
            bool more = hasMore;
            for (int page = nextPage; more; page = checked(page + 1))
            {
                var result = await readPage(page, token);
                token.ThrowIfCancellationRequested();
                rows.AddRange(result.Entries.Select(createRow));
                more = result.HasMore;
            }
            return rows;
        }
        finally
        {
            gate.Release();
        }
    }

    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count) =>
        AsyncInfo.Run(async requestToken =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                navigationToken,
                requestToken
            );
            bool entered = false;
            try
            {
                await gate.WaitAsync(cancellation.Token);
                entered = true;
                if (!HasMoreItems)
                    return new LoadMoreItemsResult();
                var result = await readPage(nextPage, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                foreach (var entry in result.Entries)
                    Add(createRow(entry));
                nextPage = checked(nextPage + 1);
                hasMore = result.HasMore;
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(CountDescription)));
                return new LoadMoreItemsResult { Count = (uint)result.Entries.Length };
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return new LoadMoreItemsResult();
            }
            catch (Exception error)
            {
                hasMore = false;
                onError(error);
                return new LoadMoreItemsResult();
            }
            finally
            {
                if (entered)
                    gate.Release();
            }
        });
}
