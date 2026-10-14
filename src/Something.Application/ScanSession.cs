using Something.Ntfs;
using Something.Statistics;
using Something.Storage;

namespace Something.Application;

/// <summary>Owns one ephemeral scan generation. Serializes every SQLite operation on workers.</summary>
public sealed class ScanSession : IAsyncDisposable
{
    private readonly IDisposable database;
    private readonly ScanStore store;
    private readonly SemaphoreSlim gate = new(1);
    private bool disposed;
    public Guid ScanId { get; }
    public string Drive { get; }
    public FileReference Root { get; }
    public FolderCapacitySample RootSample { get; }
    public ulong MetadataUnresolved { get; }
    public DiagnosticDatabaseSelection Selection { get; }
    public bool IsDemo { get; }
    public TimeSpan Elapsed { get; }
    public ScanHistoryEntry? History { get; private set; }

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
        RootSample = rootSample;
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
    ) =>
        Task.Run(
            () =>
            {
                var snapshot = archive.Open(id, token);
                try
                {
                    var summary = snapshot.Store.ReadSnapshotSummary(id, token);
                    return new ScanSession(
                        snapshot,
                        snapshot.Store,
                        id,
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
                }
                catch
                {
                    snapshot.Dispose();
                    throw;
                }
            },
            token
        );

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
        bool includeTotalCount = false
    )
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(
                () =>
                    store.ReadFolderEntries(
                        ScanId,
                        parent,
                        page,
                        directoriesOnly: directoriesOnly,
                        cancellationToken: token,
                        sort: sort,
                        includeTotalCount: includeTotalCount
                    ),
                token
            );
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RankedFolder[]> ReadLargestAsync(
        FolderRankingFilter filter,
        CancellationToken token,
        FolderEntrySort sort = FolderEntrySort.AllocatedBytes
    )
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(
                () =>
                    store
                        .ReadLargestFolders(ScanId, 20, token, filter: filter, sort: sort)
                        .ToArray(),
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
            await Task.Run(database.Dispose);
        }
        finally
        {
            gate.Release();
        }
    }
}
