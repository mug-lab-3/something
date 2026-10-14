using Microsoft.EntityFrameworkCore;
using Something.Ntfs;

namespace Something.Storage;

public sealed partial class ScanStore
{
    /// <summary>Publishes completed in-memory facts and their verified aggregation in one transaction.
    /// No observation staging, readback or intermediate commits. Previous publication survives any failure.</summary>
    public Guid PublishBufferedScan(
        BufferedScan buffer,
        DateTimeOffset endedUtc,
        CancellationToken cancellationToken = default,
        Action<ScanWorkProgress>? progress = null,
        Func<ulong, ulong?>? reserveMemoryGrowth = null,
        Action<ulong>? accountMemoryLimit = null
    ) =>
        PublishBufferedScanCore(
            buffer,
            endedUtc,
            null,
            cancellationToken,
            progress: progress,
            reserveMemoryGrowth: reserveMemoryGrowth,
            accountMemoryLimit: accountMemoryLimit
        );

    internal Guid PublishHistoryBuffer(
        BufferedScan buffer,
        DateTimeOffset endedUtc,
        HistorySnapshotEntity history,
        CancellationToken token,
        Action<ScanDbContext, Guid>? prepareReadModel = null,
        Action<ScanWorkProgress>? progress = null,
        Func<ulong, ulong?>? reserveMemoryGrowth = null,
        Action<ulong>? accountMemoryLimit = null
    ) =>
        PublishBufferedScanCore(
            buffer,
            endedUtc,
            history,
            token,
            prepareReadModel,
            progress,
            reserveMemoryGrowth,
            accountMemoryLimit
        );

    private Guid PublishBufferedScanCore(
        BufferedScan buffer,
        DateTimeOffset endedUtc,
        HistorySnapshotEntity? history,
        CancellationToken cancellationToken,
        Action<ScanDbContext, Guid>? prepareReadModel = null,
        Action<ScanWorkProgress>? progress = null,
        Func<ulong, ulong?>? reserveMemoryGrowth = null,
        Action<ulong>? accountMemoryLimit = null
    ) =>
        context is DuckScanDbContext duck
            ? DuckMemoryRecovery.Run(
                duck,
                report =>
                    PublishBufferedScanOnce(
                        buffer,
                        endedUtc,
                        history,
                        cancellationToken,
                        prepareReadModel,
                        report
                    ),
                reserveMemoryGrowth,
                progress,
                cancellationToken,
                accountMemoryLimit
            )
            : PublishBufferedScanOnce(
                buffer,
                endedUtc,
                history,
                cancellationToken,
                prepareReadModel,
                progress
            );

    private Guid PublishBufferedScanOnce(
        BufferedScan buffer,
        DateTimeOffset endedUtc,
        HistorySnapshotEntity? history,
        CancellationToken cancellationToken,
        Action<ScanDbContext, Guid>? prepareReadModel = null,
        Action<ScanWorkProgress>? progress = null
    )
    {
        ArgumentNullException.ThrowIfNull(buffer);
        cancellationToken.ThrowIfCancellationRequested();
        var result = buffer.Aggregation;
        var reporter = new ScanWorkReporter(progress);
        var scan = CreateScanEntity(buffer.Descriptor);
        ValidateEnd(new(scan.Id, buffer.Descriptor, StoredScanState.Running, null), endedUtc);
        Write(
            () =>
            {
                EnsureVolumeAvailable(scan.VolumeKey);
                context.Scans.Add(scan);
                // Make the parent key available within this transaction; this is not a commit.
                context.SaveChanges();
                using var rows = new ModelInsert<ObservationEntity>(context);
                using var names = new ModelInsert<FileNameEntity>(context);
                using var streams = new ModelInsert<StreamEntity>(context);
                using var folders = new ModelInsert<FolderSampleEntity>(context);
                using var historyNames = new ModelInsert<FolderNameEntity>(context);
                reporter.Start(ScanWorkStage.WritingFiles, buffer.Count);
                long completed = 0;
                foreach (var item in buffer.Observations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entity = ToEntity(scan.Id, item);
                    rows.Add(entity, cancellationToken);
                    foreach (var name in entity.Names)
                        names.Add(name, cancellationToken);
                    foreach (var stream in entity.Streams)
                        streams.Add(stream, cancellationToken);
                    reporter.Advance(++completed);
                }
                reporter.Start(ScanWorkStage.WritingFolders, result.Folders.Count);
                completed = 0;
                foreach (var sample in result.Folders.Values)
                {
                    folders.Add(ToEntity(scan.Id, sample), cancellationToken);
                    reporter.Advance(++completed);
                }
                // The traversal includes non-directories, so its denominator is all observations.
                reporter.Start(ScanWorkStage.WritingFolderNames, buffer.Count);
                completed = 0;
                foreach (var item in buffer.Observations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (
                        item.File is not { IsDirectory: true } file
                        || !result.Folders.ContainsKey(file.Reference)
                    )
                    {
                        reporter.Advance(++completed);
                        continue;
                    }
                    for (int i = 0; i < file.Names.Length; i++)
                    {
                        var name = file.Names[i];
                        historyNames.Add(
                            new()
                            {
                                ScanId = scan.Id,
                                RecordIndex = checked((long)file.Reference.RecordIndex),
                                Ordinal = i,
                                ParentIndex = checked((long)name.Parent.RecordIndex),
                                ParentSequence = name.Parent.SequenceNumber,
                                Name = name.Name,
                                Namespace = name.Namespace,
                                Attributes = name.FileAttributes,
                            },
                            cancellationToken
                        );
                    }
                    reporter.Advance(++completed);
                }
                reporter.Start(ScanWorkStage.FlushingRows);
                rows.Dispose();
                names.Dispose();
                streams.Dispose();
                folders.Dispose();
                historyNames.Dispose();
                context.Volumes.Add(ToEntity(scan.Id, result));
                scan.State = StoredScanState.Completed;
                scan.Ended = endedUtc;
                SetPublishedGeneration(scan.Id, buffer.Descriptor);
                if (history is not null)
                {
                    history.Id = scan.Id;
                    context.HistorySnapshots.Add(history);
                }
                // Optional experimental read models share the publication transaction.
                prepareReadModel?.Invoke(context, scan.Id);
                SqliteDatabaseBootstrap.OptimizeForBrowsing(context, cancellationToken);
            },
            cancellationToken,
            reporter
        );
        return scan.Id;
    }

    private void SetPublishedGeneration(Guid scanId, ScanDescriptor descriptor)
    {
        long start = checked((long)descriptor.Range.StartIndex),
            count = checked((long)descriptor.Range.RecordCount);
        var published = context.Published.SingleOrDefault(p =>
            p.VolumeKey == descriptor.VolumeKey && p.RangeStart == start && p.RangeCount == count
        );
        if (published is null)
            context.Published.Add(
                new()
                {
                    VolumeKey = descriptor.VolumeKey,
                    RangeStart = start,
                    RangeCount = count,
                    ScanId = scanId,
                }
            );
        else
            published.ScanId = scanId;
    }
}
