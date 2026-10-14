using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Something.Ntfs;
using Something.Statistics;

namespace Something.Storage;

public sealed partial class ScanStore
{
    /// <summary>Caller must have finished enumeration successfully. Recomputes staged facts; publication is atomic.</summary>
    public FolderAggregationResult CompleteScan(
        Guid scanId,
        DateTimeOffset endedUtc,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scan = RequireRunning(scanId);
        ValidateEnd(scan, endedUtc);
        var result = AggregateStage(scanId, cancellationToken);
        if (!result.Folders.Keys.Any(r => r.RecordIndex == 5))
            throw new InvalidDataException("A valid root hierarchy is required for publication.");
        foreach (var batch in result.Folders.Values.Chunk(MaximumBatchSize))
        {
            Write(
                () =>
                {
                    RequireRunning(scanId);
                    long[] indices = [.. batch.Select(s => checked((long)s.Reference.RecordIndex))];
                    var existing = context
                        .Folders.Where(f => f.ScanId == scanId && indices.Contains(f.RecordIndex))
                        .ToDictionary(f => f.RecordIndex);
                    using var samples = new ModelInsert<FolderSampleEntity>(context);
                    using var folderNames = new ModelInsert<FolderNameEntity>(context);
                    foreach (var sample in batch)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entity = ToEntity(scanId, sample);
                        if (existing.TryGetValue(entity.RecordIndex, out var old))
                            context.Entry(old).CurrentValues.SetValues(entity);
                        else
                            samples.Add(entity, cancellationToken);
                    }
                    var savedNames = context
                        .FolderNames.AsNoTracking()
                        .Where(n => n.ScanId == scanId && indices.Contains(n.RecordIndex))
                        .Select(n => new { n.RecordIndex, n.Ordinal })
                        .ToArray()
                        .Select(n => (n.RecordIndex, n.Ordinal))
                        .ToHashSet();
                    var names = context
                        .Names.AsNoTracking()
                        .Where(n => n.ScanId == scanId && indices.Contains(n.RecordIndex))
                        .ToArray();
                    foreach (var name in names)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (savedNames.Contains((name.RecordIndex, name.Ordinal)))
                            continue;
                        folderNames.Add(
                            new()
                            {
                                ScanId = scanId,
                                RecordIndex = name.RecordIndex,
                                Ordinal = name.Ordinal,
                                ParentIndex = name.ParentIndex,
                                ParentSequence = name.ParentSequence,
                                Name = name.Name,
                                Namespace = name.Namespace,
                                Attributes = name.Attributes,
                            },
                            cancellationToken
                        );
                    }
                },
                cancellationToken
            );
        }
        Write(
            () =>
            {
                RequireRunning(scanId);
                var summary = ToEntity(scanId, result);
                var old = context.Volumes.SingleOrDefault(v => v.ScanId == scanId);
                if (old is null)
                    context.Volumes.Add(summary);
                else
                    context.Entry(old).CurrentValues.SetValues(summary);
                var entity = context.Scans.Single(s => s.Id == scanId);
                entity.State = StoredScanState.Completed;
                entity.Ended = endedUtc;
                SetPublishedGeneration(scanId, scan.Descriptor);
                SqliteDatabaseBootstrap.OptimizeForBrowsing(context, cancellationToken);
            },
            cancellationToken
        );
        return result;
    }

    private static FolderSampleEntity ToEntity(Guid scanId, FolderCapacitySample sample) =>
        new()
        {
            ScanId = scanId,
            RecordIndex = checked((long)sample.Reference.RecordIndex),
            Sequence = sample.Reference.SequenceNumber,
            DirectLogical = sample.DirectData.LogicalBytes,
            DirectAllocated = sample.DirectData.AllocatedBytes,
            SharedLogical = sample.SharedData.LogicalBytes,
            SharedAllocated = sample.SharedData.AllocatedBytes,
            DirectoryLogical = sample.DirectoryData.LogicalBytes,
            DirectoryAllocated = sample.DirectoryData.AllocatedBytes,
            SubtreeLogical = sample.SubtreeData.LogicalBytes,
            SubtreeAllocated = sample.SubtreeData.AllocatedBytes,
            AssignedCount = checked((long)sample.AssignedFileCount),
            SubtreeCount = checked((long)sample.SubtreeFileCount),
            DirectUnresolved = checked((long)sample.DirectUnresolvedCount),
            SubtreeUnresolved = checked((long)sample.SubtreeUnresolvedCount),
            DirectoryObserved = sample.DirectoryCapacityObserved,
            UnobservedDirectories = checked((long)sample.SubtreeUnobservedDirectoryCount),
            EntryDirectLogical = sample.Entries?.DirectData.LogicalBytes,
            EntryDirectAllocated = sample.Entries?.DirectData.AllocatedBytes,
            EntrySubtreeLogical = sample.Entries?.SubtreeData.LogicalBytes,
            EntrySubtreeAllocated = sample.Entries?.SubtreeData.AllocatedBytes,
            EntryDirectFiles = checked((long?)sample.Entries?.DirectFileCount),
            EntrySubtreeFiles = checked((long?)sample.Entries?.SubtreeFileCount),
            EntryDirectUnresolved = checked((long?)sample.Entries?.DirectUnresolvedCount),
            EntrySubtreeUnresolved = checked((long?)sample.Entries?.SubtreeUnresolvedCount),
        };

    private static VolumeSampleEntity ToEntity(Guid scanId, FolderAggregationResult result) =>
        new()
        {
            ScanId = scanId,
            MetadataLogical = result.Metadata.KnownData.LogicalBytes,
            MetadataAllocated = result.Metadata.KnownData.AllocatedBytes,
            MetadataResolved = checked((long)result.Metadata.ResolvedCount),
            MetadataUnresolved = checked((long)result.Metadata.UnresolvedCount),
            UnattributedLogical = result.Unattributed.KnownData.LogicalBytes,
            UnattributedAllocated = result.Unattributed.KnownData.AllocatedBytes,
            UnattributedResolved = checked((long)result.Unattributed.ResolvedCount),
            UnattributedUnresolved = checked((long)result.Unattributed.UnresolvedCount),
            UnknownAttribution = result.HasUnknownAttribution,
        };

    public (FolderCapacitySample Root, ulong MetadataUnresolved) ReadSnapshotSummary(
        Guid scanId,
        CancellationToken token = default
    )
    {
        token.ThrowIfCancellationRequested();
        RequireCompleted(scanId);
        var root = context
            .Folders.AsNoTracking()
            .Single(row => row.ScanId == scanId && row.RecordIndex == 5);
        var volume = context.Volumes.AsNoTracking().Single(row => row.ScanId == scanId);
        return (ReadFolderSample(root), checked((ulong)volume.MetadataUnresolved));
    }

    private static FolderCapacitySample ReadFolderSample(FolderSampleEntity row) =>
        new(
            FileReference.Create(checked((ulong)row.RecordIndex), row.Sequence),
            new(row.DirectLogical, row.DirectAllocated),
            new(row.SharedLogical, row.SharedAllocated),
            new(row.DirectoryLogical, row.DirectoryAllocated),
            new(row.SubtreeLogical, row.SubtreeAllocated),
            checked((ulong)row.AssignedCount),
            checked((ulong)row.SubtreeCount),
            checked((ulong)row.DirectUnresolved),
            checked((ulong)row.SubtreeUnresolved),
            row.DirectoryObserved,
            checked((ulong)row.UnobservedDirectories)
        )
        {
            Entries = ReadEntrySample(row),
        };

    public FolderAggregationResult ReadAggregation(
        Guid scanId,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireCompleted(scanId);
        var folders = ImmutableDictionary.CreateBuilder<FileReference, FolderCapacitySample>();
        foreach (
            var row in context.Folders.AsNoTracking().Where(f => f.ScanId == scanId).AsEnumerable()
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = FileReference.Create(checked((ulong)row.RecordIndex), row.Sequence);
            folders.Add(reference, ReadFolderSample(row));
        }
        var summary =
            context.Volumes.AsNoTracking().SingleOrDefault(v => v.ScanId == scanId)
            ?? throw new InvalidDataException("Completed scan lacks volume aggregation.");
        return new(
            folders.ToImmutable(),
            new(
                new(summary.MetadataLogical, summary.MetadataAllocated),
                checked((ulong)summary.MetadataResolved),
                checked((ulong)summary.MetadataUnresolved)
            ),
            new(
                new(summary.UnattributedLogical, summary.UnattributedAllocated),
                checked((ulong)summary.UnattributedResolved),
                checked((ulong)summary.UnattributedUnresolved)
            ),
            summary.UnknownAttribution
        );
    }

    private static FolderEntrySample? ReadEntrySample(FolderSampleEntity row) =>
        row.EntryDirectLogical is null
            ? null
            : new(
                new(
                    row.EntryDirectLogical.Value,
                    row.EntryDirectAllocated
                        ?? throw new InvalidDataException("Missing entry capacity.")
                ),
                new(
                    row.EntrySubtreeLogical
                        ?? throw new InvalidDataException("Missing entry capacity."),
                    row.EntrySubtreeAllocated
                        ?? throw new InvalidDataException("Missing entry capacity.")
                ),
                checked(
                    (ulong)(
                        row.EntryDirectFiles
                        ?? throw new InvalidDataException("Missing entry count.")
                    )
                ),
                checked(
                    (ulong)(
                        row.EntrySubtreeFiles
                        ?? throw new InvalidDataException("Missing entry count.")
                    )
                ),
                checked(
                    (ulong)(
                        row.EntryDirectUnresolved
                        ?? throw new InvalidDataException("Missing entry count.")
                    )
                ),
                checked(
                    (ulong)(
                        row.EntrySubtreeUnresolved
                        ?? throw new InvalidDataException("Missing entry count.")
                    )
                )
            );

    public ImmutableArray<NtfsFileName> ReadHistoricalFolderNames(
        Guid scanId,
        FileReference folder,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireCompleted(scanId);
        long index = checked((long)folder.RecordIndex);
        var rows =
            from n in context.FolderNames.AsNoTracking()
            join f in context.Folders.AsNoTracking()
                on new { n.ScanId, n.RecordIndex } equals new { f.ScanId, f.RecordIndex }
            where
                n.ScanId == scanId && n.RecordIndex == index && f.Sequence == folder.SequenceNumber
            orderby n.Ordinal
            select n;
        var names = ImmutableArray.CreateBuilder<NtfsFileName>();
        foreach (var row in rows.AsEnumerable())
        {
            cancellationToken.ThrowIfCancellationRequested();
            names.Add(
                new(
                    FileReference.Create(checked((ulong)row.ParentIndex), row.ParentSequence),
                    row.Name,
                    row.Namespace,
                    row.Attributes
                )
            );
        }
        return names.ToImmutable();
    }

    private void RequireCompleted(Guid id)
    {
        if (GetScan(id).State != StoredScanState.Completed)
            throw new InvalidOperationException("Only completed scans may be read as history.");
    }

    /// <summary>Deletes only superseded details within the application's database; preserves folder history.</summary>
    public int PruneDetailsBatch(Guid scanId, CancellationToken cancellationToken = default)
    {
        int removed = 0;
        Write(
            () =>
            {
                if (GetScan(scanId).State == StoredScanState.Running)
                    throw new InvalidOperationException("Cannot prune a running scan.");
                if (context.Published.Any(p => p.ScanId == scanId))
                    throw new InvalidOperationException("Cannot prune published details.");
                var batch = context
                    .Observations.AsNoTracking()
                    .Where(o => o.ScanId == scanId)
                    .OrderBy(o => o.RecordIndex)
                    .Take(MaximumBatchSize)
                    .Select(o => o.RecordIndex)
                    .ToArray();
                removed = context
                    .Observations.Where(o => o.ScanId == scanId && batch.Contains(o.RecordIndex))
                    .ExecuteDelete();
            },
            cancellationToken
        );
        return removed;
    }
}
