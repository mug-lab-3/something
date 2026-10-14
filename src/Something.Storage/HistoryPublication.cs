using Microsoft.EntityFrameworkCore;
using Something.Ntfs;
using Something.Statistics;

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
        if (context is SharedHistoryDbContext)
        {
            var buffer = new BufferedScan(source.GetScan(scan.Id).Descriptor);
            foreach (
                var row in source
                    .context.Observations.AsNoTracking()
                    .Where(row => row.ScanId == scan.Id)
                    .Include(row => row.Names)
                    .Include(row => row.Streams)
                    .AsSplitQuery()
            )
            {
                token.ThrowIfCancellationRequested();
                FileCapacityObservation? file = row.Sequence is { } sequence
                    ? new(
                        FileReference.Create(checked((ulong)row.RecordIndex), sequence),
                        row.IsDirectory!.Value,
                        row.Domain!.Value,
                        row.Resolution!.Value,
                        [.. row.Names.OrderBy(n => n.Ordinal).Select(ToName)],
                        [
                            .. row
                                .Streams.OrderBy(s => s.Ordinal)
                                .Select(s => new StreamCapacity(s.Name, s.Logical, s.Allocated)),
                        ],
                        row.LinksComplete!.Value
                    )
                    {
                        ObservedHardLinkCount = row.ObservedLinkCount,
                    }
                    : null;
                buffer.Add(
                    new StagedObservation(
                        checked((ulong)row.RecordIndex),
                        row.Kind,
                        row.Status,
                        file
                    ),
                    token
                );
            }
            buffer.Complete(token);
            PublishHistoryBuffer(buffer, scan.Ended!.Value, metadata, token);
            if (
                source.context is DuckScanDbContext
                && source.ReadTimings(scan.Id, token) is { } timing
            )
                SaveTimings(scan.Id, timing, token);
            return;
        }
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
        if (context is SharedHistoryDbContext shared)
        {
            context.StepTimings.Where(row => row.ScanId == id).ExecuteDelete();
            context.Timings.Where(row => row.ScanId == id).ExecuteDelete();
            shared.Listings.Where(row => row.ScanId == id).ExecuteDelete();
            shared.Auxiliary.Where(row => row.ScanId == id).ExecuteDelete();
            shared.SharedFolders.Where(row => row.ScanId == id).ExecuteDelete();
            context.Volumes.Where(row => row.ScanId == id).ExecuteDelete();
            SharedHistoryPublication.Collect(shared);
        }
        else if (context is DuckScanDbContext)
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
