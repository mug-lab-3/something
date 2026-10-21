using Something.Application.Browsing;
using Something.Application.History;
using Something.Application.Memory;
using Something.Application.Repositories;
using Something.Ntfs;
using Something.Statistics;

namespace Something.Application;

/// <summary>Owns one scan generation and its display projection. Serializes database operations on workers.</summary>
public sealed partial class ScanSession : IAsyncDisposable
{
    private readonly IScanSnapshot snapshot;

    private readonly SemaphoreSlim gate = new(1);
    private bool disposed;
    private readonly DateTimeOffset? completedLocal;
    public Guid ScanId { get; }
    public string Drive { get; }
    public FileReference Root { get; }
    public string VolumeKey { get; }
    public ulong VolumeSerial { get; }
    public FolderCapacitySample RootSample { get; private set; }
    public ulong MetadataUnresolved { get; }
    public DiagnosticDatabaseSelection Selection { get; }
    public bool IsDemo { get; }
    public TimeSpan Elapsed { get; private set; }
    public DateTimeOffset? CompletedLocal => History?.CompletedLocal ?? completedLocal;
    public ScanTimingSnapshot? Timings { get; private set; }
    public ScanHistoryEntry? History { get; private set; }

    internal void SetTimings(ScanTimingSnapshot timing, ScanHistoryEntry? history = null)
    {
        Timings = timing;
        Elapsed = timing.TotalElapsed;
        if (history is not null)
            History = history;
    }

    public async Task<ScanHistoryEntry> SaveHistoryAsync(
        HistoryService history,
        CancellationToken token
    )
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var scans = history.Repositories.Scans;
            var entry = await Task.Run(() => scans.Save(snapshot, Drive, Elapsed, token), token);
            History = entry;
            return entry;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Takes over <paramref name="snapshot"/>; it is released when the session is disposed.</summary>
    internal ScanSession(
        IScanSnapshot snapshot,
        string drive,
        FileReference root,
        FolderCapacitySample rootSample,
        ulong metadataUnresolved,
        DiagnosticDatabaseSelection selection,
        bool demo,
        TimeSpan elapsed,
        ScanHistoryEntry? history = null
    )
    {
        this.snapshot = snapshot;
        History = history;
        ScanId = snapshot.ScanId;
        Drive = drive;
        Root = root;
        var description = snapshot.Describe();
        completedLocal = description.EndedUtc?.ToLocalTime();
        VolumeKey = description.VolumeKey;
        VolumeSerial = description.VolumeSerial;
        RootSample = unfilteredRootSample = rootSample;
        MetadataUnresolved = metadataUnresolved;
        Selection = selection;
        IsDemo = demo;
        Elapsed = elapsed;
    }

    internal static ScanSession FromHistory(
        IHistorySnapshot snapshot,
        string drive,
        CancellationToken token
    )
    {
        try
        {
            var summary = snapshot.ReadSummary(token);
            var session = new ScanSession(
                snapshot,
                drive,
                summary.Root.Reference,
                summary.Root,
                summary.MetadataUnresolved,
                DiagnosticDatabaseSelector.Select(
                    snapshot.SizeBytes,
                    null,
                    DiagnosticDatabasePreference.File
                ),
                false,
                snapshot.Entry.Elapsed,
                snapshot.Entry
            );
            if (snapshot.ReadTimings(token) is { } timing)
                session.SetTimings(timing);
            return session;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    public async Task<FileReference?> ReadParentAsync(
        FileReference folder,
        string visibleName,
        CancellationToken token
    )
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(
                () => snapshot.ReadParentFolder(folder, visibleName, token),
                token
            );
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<FolderEntryPage> ReadFolderAsync(
        FileReference parent,
        int page,
        bool directoriesOnly,
        CancellationToken token,
        FolderEntrySort sort = FolderEntrySort.AllocatedBytes,
        bool includeTotalCount = false,
        bool descending = true
    )
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var cacheKey = new RootPageKey(
                visibility,
                directoriesOnly,
                sort,
                includeTotalCount,
                descending
            );
            token.ThrowIfCancellationRequested();
            if (parent == Root && page == 0 && rootPageCache.TryGetValue(cacheKey, out var cached))
                return cached;
            var result = await Task.Run(
                () =>
                    visibleScan is not null
                        ? ReadVisibleFolder(
                            parent,
                            page,
                            directoriesOnly,
                            token,
                            sort,
                            includeTotalCount,
                            descending
                        )
                        : snapshot.ReadFolderEntries(
                            parent,
                            page,
                            directoriesOnly,
                            sort,
                            includeTotalCount,
                            descending,
                            token
                        ),
                token
            );
            token.ThrowIfCancellationRequested();
            if (parent == Root && page == 0)
                rootPageCache[cacheKey] = result;
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RankedFolder[]> ReadFolderAnalysisAsync(
        FolderRankingFilter filter,
        CancellationToken token,
        FolderEntrySort sort = FolderEntrySort.AllocatedBytes
    )
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(() => ReadAnalysis(filter, token, sort), token);
        }
        finally
        {
            gate.Release();
        }
    }

    private IReadOnlyDictionary<FileReference, uint?>? folderAttributeCache;

    public async Task<IReadOnlyDictionary<FileReference, uint?>> ReadFolderAttributesAsync(
        CancellationToken token
    )
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return folderAttributeCache ??= await Task.Run(
                () =>
                    ReadVisibilityFolders(token)
                        .ToDictionary(f => f.Reference, f => f.StandardFileAttributes),
                token
            );
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
            ClearDisplayCaches();
            await Task.Run(snapshot.Dispose);
        }
        finally
        {
            gate.Release();
        }
    }
}
