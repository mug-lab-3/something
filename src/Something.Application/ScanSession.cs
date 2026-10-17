using Something.Ntfs;
using Something.Statistics;
using Something.Storage;

namespace Something.Application;

/// <summary>Owns one scan generation and its display projection. Serializes database operations on workers.</summary>
public sealed partial class ScanSession : IAsyncDisposable
{
    private readonly IDisposable database;
    private readonly ScanStore store;
    private readonly SemaphoreSlim gate = new(1);
    private bool disposed;
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
        ScanHistoryArchive archive,
        CancellationToken token
    )
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var entry = await Task.Run(
                () => archive.Save(store, ScanId, Drive, Elapsed, token),
                token
            );
            History = entry;
            return entry;
        }
        finally
        {
            gate.Release();
        }
    }

    internal ScanSession(
        TemporaryScanDatabase database,
        Guid id,
        string drive,
        FileReference root,
        FolderCapacitySample rootSample,
        ulong metadataUnresolved,
        DiagnosticDatabaseSelection selection,
        bool demo,
        TimeSpan elapsed,
        ScanHistoryEntry? history = null
    )
        : this(
            database,
            database.Store,
            id,
            drive,
            root,
            rootSample,
            metadataUnresolved,
            selection,
            demo,
            elapsed,
            history
        ) { }

    private ScanSession(
        IDisposable database,
        ScanStore store,
        Guid id,
        string drive,
        FileReference root,
        FolderCapacitySample rootSample,
        ulong metadataUnresolved,
        DiagnosticDatabaseSelection selection,
        bool demo,
        TimeSpan elapsed,
        ScanHistoryEntry? history
    )
    {
        this.database = database;
        this.store = store;
        History = history;
        ScanId = id;
        Drive = drive;
        Root = root;
        var descriptor = store.GetScan(id).Descriptor;
        VolumeKey = descriptor.VolumeKey;
        VolumeSerial = descriptor.VolumeSerial;
        RootSample = unfilteredRootSample = rootSample;
        MetadataUnresolved = metadataUnresolved;
        Selection = selection;
        IsDemo = demo;
        Elapsed = elapsed;
    }

    public static Task<ScanSession> OpenHistoryAsync(
        ScanHistoryArchive archive,
        Guid id,
        string drive,
        CancellationToken token
    ) => Task.Run(() => FromHistory(archive.Open(id, token), drive, token), token);

    internal static ScanSession FromHistory(
        HistorySnapshot snapshot,
        string drive,
        CancellationToken token
    )
    {
        try
        {
            var summary = snapshot.Store.ReadSnapshotSummary(snapshot.Entry.Id, token);
            var session = new ScanSession(
                snapshot,
                snapshot.Store,
                snapshot.Entry.Id,
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
            if (snapshot.Store.ReadTimings(snapshot.Entry.Id, token) is { } timing)
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
                () => store.ReadParentFolder(ScanId, folder, visibleName, token),
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
                        : store.ReadFolderEntries(
                            ScanId,
                            parent,
                            page,
                            directoriesOnly: directoriesOnly,
                            cancellationToken: token,
                            sort: sort,
                            includeTotalCount: includeTotalCount,
                            descending: descending
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
            await Task.Run(database.Dispose);
        }
        finally
        {
            gate.Release();
        }
    }
}
