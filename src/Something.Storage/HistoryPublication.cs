using Microsoft.EntityFrameworkCore;

namespace Something.Storage;

public sealed partial class ScanStore
{
    internal void CopyHistoryGeneration(
        ScanStore source,
        HistorySnapshotEntity metadata,
        CancellationToken token
    )
    {
        if (ReferenceEquals(source, this))
            throw new ArgumentException("Import source must be a separate store.", nameof(source));
        var scan = source.context.Scans.AsNoTracking().Single(row => row.Id == metadata.Id);
        if (
            scan.State != StoredScanState.Completed
            || scan.VolumeKey != metadata.VolumeKey
            || scan.Ended?.UtcTicks != metadata.CompletedUtcTicks
        )
            throw new InvalidDataException("History metadata and source generation disagree.");
        Write(
            () =>
            {
                context.Scans.Add(scan);
                context.SaveChanges();
                Copy(
                    source.context.Observations.AsNoTracking().Where(row => row.ScanId == scan.Id),
                    token
                );
                Copy(
                    source.context.Names.AsNoTracking().Where(row => row.ScanId == scan.Id),
                    token
                );
                Copy(
                    source.context.Streams.AsNoTracking().Where(row => row.ScanId == scan.Id),
                    token
                );
                Copy(
                    source.context.Folders.AsNoTracking().Where(row => row.ScanId == scan.Id),
                    token
                );
                Copy(
                    source.context.FolderNames.AsNoTracking().Where(row => row.ScanId == scan.Id),
                    token
                );
                Copy(
                    source.context.Volumes.AsNoTracking().Where(row => row.ScanId == scan.Id),
                    token
                );
                if (source.context is DuckScanDbContext)
                {
                    Copy(
                        source.context.Timings.AsNoTracking().Where(row => row.ScanId == scan.Id),
                        token
                    );
                    Copy(
                        source
                            .context.StepTimings.AsNoTracking()
                            .Where(row => row.ScanId == scan.Id),
                        token
                    );
                }
                context.HistorySnapshots.Add(metadata);
                SetPublishedGeneration(scan.Id, source.GetScan(scan.Id).Descriptor);
                SqliteDatabaseBootstrap.OptimizeForBrowsing(context, token);
            },
            token
        );
    }

    private void Copy<T>(IQueryable<T> source, CancellationToken token)
        where T : class
    {
        using var insert = new ModelInsert<T>(context);
        foreach (var row in source.AsEnumerable())
            insert.Add(row, token);
    }

    internal ScanDbContext HistoryContext => context;

    internal void DeleteHistoryGeneration(Guid id)
    {
        context.Published.Where(row => row.ScanId == id).ExecuteDelete();
        if (context is DuckScanDbContext)
        {
            context.StepTimings.Where(row => row.ScanId == id).ExecuteDelete();
            context.Timings.Where(row => row.ScanId == id).ExecuteDelete();
            context.Browser.Where(row => row.ScanId == id).ExecuteDelete();
            context.FolderNames.Where(row => row.ScanId == id).ExecuteDelete();
            context.Names.Where(row => row.ScanId == id).ExecuteDelete();
            context.Streams.Where(row => row.ScanId == id).ExecuteDelete();
            context.Folders.Where(row => row.ScanId == id).ExecuteDelete();
            context.Observations.Where(row => row.ScanId == id).ExecuteDelete();
            context.Volumes.Where(row => row.ScanId == id).ExecuteDelete();
        }
        context.Scans.Where(row => row.Id == id).ExecuteDelete(); // All generation facts cascade.
        context.HistorySnapshots.Where(row => row.Id == id).ExecuteDelete();
    }
}
