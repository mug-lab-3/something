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
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(buffer);
        cancellationToken.ThrowIfCancellationRequested();
        var result = buffer.Aggregation;
        var scan = CreateScanEntity(buffer.Descriptor);
        ValidateEnd(new(scan.Id, buffer.Descriptor, StoredScanState.Running, null), endedUtc);
        Write(
            () =>
            {
                context.Scans.Add(scan);
                // Make the parent key available within this transaction; this is not a commit.
                context.SaveChanges();
                using var rows = new ModelInsert<ObservationEntity>(context);
                using var names = new ModelInsert<FileNameEntity>(context);
                using var streams = new ModelInsert<StreamEntity>(context);
                using var folders = new ModelInsert<FolderSampleEntity>(context);
                using var historyNames = new ModelInsert<FolderNameEntity>(context);
                foreach (var item in buffer.Observations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entity = ToEntity(scan.Id, item);
                    rows.Add(entity, cancellationToken);
                    foreach (var name in entity.Names)
                        names.Add(name, cancellationToken);
                    foreach (var stream in entity.Streams)
                        streams.Add(stream, cancellationToken);
                }
                foreach (var sample in result.Folders.Values)
                    folders.Add(ToEntity(scan.Id, sample), cancellationToken);
                foreach (var item in buffer.Observations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (
                        item.File is not { IsDirectory: true } file
                        || !result.Folders.ContainsKey(file.Reference)
                    )
                        continue;
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
                }
                context.Volumes.Add(ToEntity(scan.Id, result));
                scan.State = StoredScanState.Completed;
                scan.Ended = endedUtc;
                SetPublishedGeneration(scan.Id, buffer.Descriptor);
            },
            cancellationToken
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
