using Microsoft.EntityFrameworkCore;
using Something.Application.Scanning;

namespace Something.Storage;

internal sealed partial class ScanStore
{
    /// <summary>Publishes completed in-memory facts and their verified aggregation in one transaction.
    /// No observation staging, readback or intermediate commits. Previous publication survives any failure.</summary>
    public Guid PublishBufferedScan(
        CompletedScan completedScan,
        DateTimeOffset endedUtc,
        CancellationToken cancellationToken = default,
        Action<ScanWorkProgress>? progress = null,
        Func<ulong, ulong?>? reserveMemoryGrowth = null,
        Action<ulong>? accountMemoryLimit = null
    ) =>
        PublishBufferedScanCore(
            completedScan,
            endedUtc,
            null,
            cancellationToken,
            progress: progress,
            reserveMemoryGrowth: reserveMemoryGrowth,
            accountMemoryLimit: accountMemoryLimit
        );

    internal Guid PublishHistoryBuffer(
        CompletedScan completedScan,
        DateTimeOffset endedUtc,
        HistorySnapshotEntity history,
        CancellationToken token,
        Action<ScanDbContext, Guid>? prepareReadModel = null,
        Action<ScanWorkProgress>? progress = null,
        Func<ulong, ulong?>? reserveMemoryGrowth = null,
        Action<ulong>? accountMemoryLimit = null
    ) =>
        PublishBufferedScanCore(
            completedScan,
            endedUtc,
            history,
            token,
            prepareReadModel,
            progress,
            reserveMemoryGrowth,
            accountMemoryLimit
        );

    private Guid PublishBufferedScanCore(
        CompletedScan completedScan,
        DateTimeOffset endedUtc,
        HistorySnapshotEntity? history,
        CancellationToken cancellationToken,
        Action<ScanDbContext, Guid>? prepareReadModel = null,
        Action<ScanWorkProgress>? progress = null,
        Func<ulong, ulong?>? reserveMemoryGrowth = null,
        Action<ulong>? accountMemoryLimit = null
    )
    {
        if (context is not DuckScanDbContext duck)
            return PublishBufferedScanOnce(
                completedScan,
                endedUtc,
                history,
                cancellationToken,
                prepareReadModel,
                progress
            );
        Guid attempted = Guid.Empty;
        return DuckMemoryRecovery.Run(
            duck,
            report =>
                PublishBufferedScanOnce(
                    completedScan,
                    endedUtc,
                    history,
                    cancellationToken,
                    prepareReadModel,
                    report,
                    id => attempted = id
                ),
            reserveMemoryGrowth,
            progress,
            cancellationToken,
            accountMemoryLimit,
            // Publication sets Completed in the same transaction, so a completed row proves the commit.
            () =>
                (
                    attempted != Guid.Empty
                        && context
                            .Scans.AsNoTracking()
                            .Any(s => s.Id == attempted && s.State == StoredScanState.Completed),
                    attempted
                )
        );
    }

    private Guid PublishBufferedScanOnce(
        CompletedScan completedScan,
        DateTimeOffset endedUtc,
        HistorySnapshotEntity? history,
        CancellationToken cancellationToken,
        Action<ScanDbContext, Guid>? prepareReadModel = null,
        Action<ScanWorkProgress>? progress = null,
        Action<Guid>? attempting = null
    )
    {
        ArgumentNullException.ThrowIfNull(completedScan);
        cancellationToken.ThrowIfCancellationRequested();
        var result = completedScan.Aggregation;
        var reporter = new ScanWorkReporter(progress);
        var scan = CreateScanEntity(completedScan.Descriptor);
        if (history is { Id: var importedId } && importedId != Guid.Empty)
            scan.Id = importedId;
        attempting?.Invoke(scan.Id);
        ValidateEnd(
            new(scan.Id, completedScan.Descriptor, StoredScanState.Running, null),
            endedUtc
        );
        Write(
            () =>
            {
                EnsureVolumeAvailable(scan.VolumeKey);
                context.Scans.Add(scan);
                // Shared history numbers generations under the database gate; numbers only grow,
                // so a new generation never reuses the number of a stored state version.
                if (context is SharedHistoryDbContext numbered)
                    numbered
                        .Entry(scan)
                        .Property(SharedHistoryDbContext.GenerationNumber)
                        .CurrentValue =
                        (
                            numbered.Scans.Max(s =>
                                (int?)EF.Property<int>(s, SharedHistoryDbContext.GenerationNumber)
                            ) ?? 0
                        ) + 1;
                // Make the parent key available within this transaction; this is not a commit.
                context.SaveChanges();
                try
                {
                    if (context is SharedHistoryDbContext shared)
                        SharedHistoryPublication.Publish(
                            shared,
                            scan,
                            completedScan,
                            cancellationToken,
                            reporter
                        );
                    else
                        WriteFacts(scan.Id, completedScan, cancellationToken, reporter);
                    context.Volumes.Add(ToEntity(scan.Id, result));
                    scan.State = StoredScanState.Completed;
                    scan.Ended = endedUtc;
                    SetPublishedGeneration(scan.Id, completedScan.Descriptor);
                    if (history is not null)
                    {
                        history.Id = scan.Id;
                        context.HistorySnapshots.Add(history);
                    }
                    // Optional experimental read models share the publication transaction.
                    prepareReadModel?.Invoke(context, scan.Id);
                    SqliteDatabaseBootstrap.OptimizeForBrowsing(context, cancellationToken);
                }
                catch (Exception error)
                {
                    if (context is DuckScanDbContext duck)
                        duck.AppendFailure ??= error;
                    throw;
                }
            },
            cancellationToken,
            reporter
        );
        return scan.Id;
    }

    private void WriteFacts(
        Guid id,
        CompletedScan completedScan,
        CancellationToken cancellationToken,
        ScanWorkReporter reporter
    )
    {
        var result = completedScan.Aggregation;
        using var rows = new ModelInsert<ObservationEntity>(context);
        using var names = new ModelInsert<FileNameEntity>(context);
        using var streams = new ModelInsert<StreamEntity>(context);
        using var folders = new ModelInsert<FolderSampleEntity>(context);
        using var historyNames = new ModelInsert<FolderNameEntity>(context);
        reporter.Start(ScanWorkStage.WritingFiles, completedScan.Observations.Count);
        long completed = 0;
        foreach (var item in completedScan.Observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entity = ToEntity(id, item);
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
            folders.Add(ToEntity(id, sample), cancellationToken);
            reporter.Advance(++completed);
        }
        // The traversal includes non-directories, so its denominator is all observations.
        reporter.Start(ScanWorkStage.WritingFolderNames, completedScan.Observations.Count);
        completed = 0;
        foreach (var item in completedScan.Observations)
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
                        ScanId = id,
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
