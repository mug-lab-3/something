using Something.Ntfs;
using Something.Statistics;
using Something.Storage;

namespace Something.Application;

/// <summary>Owns one ephemeral scan generation. Serializes every SQLite operation on workers.</summary>
public sealed class ScanSession : IAsyncDisposable
{
    private readonly TemporaryScanDatabase database;
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

    internal ScanSession(
        TemporaryScanDatabase database,
        Guid id,
        string drive,
        FileReference root,
        FolderCapacitySample rootSample,
        ulong metadataUnresolved,
        DiagnosticDatabaseSelection selection,
        bool demo,
        TimeSpan elapsed
    )
    {
        this.database = database;
        ScanId = id;
        Drive = drive;
        Root = root;
        RootSample = rootSample;
        MetadataUnresolved = metadataUnresolved;
        Selection = selection;
        IsDemo = demo;
        Elapsed = elapsed;
    }

    public async Task<FolderEntryPage> ReadFolderAsync(
        FileReference parent,
        int page,
        bool directoriesOnly,
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
                    database.Store.ReadFolderEntries(
                        ScanId,
                        parent,
                        page,
                        directoriesOnly: directoriesOnly,
                        cancellationToken: token,
                        sort: sort
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
        CancellationToken token
    )
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(
                () =>
                    database.Store.ReadLargestFolders(ScanId, 20, token, filter: filter).ToArray(),
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
