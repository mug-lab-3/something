using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Something.Ntfs;
using Something.Statistics;

namespace Something.Storage;

/// <summary>Single-owner storage. Call off the UI thread; inspected paths never select the database.</summary>
public sealed partial class ScanStore : IDisposable
{
    public const int MaximumBatchSize = 1000;
    private readonly ScanDbContext context;

    private ScanStore(ScanDbContext context) => this.context = context;

    public static ScanStore OpenDefault(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
            throw new InvalidOperationException("Application data directory is unavailable.");
        string directory = Path.Combine(root, "Something");
        Directory.CreateDirectory(directory);
        return OpenTrustedPath(Path.Combine(directory, "history.sqlite3"), cancellationToken);
    }

    internal static ScanStore OpenTrustedPath(
        string path,
        CancellationToken cancellationToken = default
    ) => new(SqliteDatabaseBootstrap.Open(path, cancellationToken));

    public Guid BeginScan(ScanDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        if (
            string.IsNullOrWhiteSpace(descriptor.VolumeKey)
            || descriptor.Epoch == Guid.Empty
            || descriptor.Range.RecordCount == 0
            || descriptor.StartedUtc.Offset != TimeSpan.Zero
        )
            throw new ArgumentException(
                "Scan requires a volume identity, epoch, nonempty range and UTC start.",
                nameof(descriptor)
            );
        var scan = new ScanEntity
        {
            Id = Guid.NewGuid(),
            VolumeKey = descriptor.VolumeKey,
            Epoch = descriptor.Epoch,
            Serial = descriptor.VolumeSerial,
            RangeStart = checked((long)descriptor.Range.StartIndex),
            RangeCount = checked((long)descriptor.Range.RecordCount),
            Started = descriptor.StartedUtc,
            State = StoredScanState.Running,
        };
        Write(() => context.Scans.Add(scan), cancellationToken);
        return scan.Id;
    }

    public StoredScan GetScan(Guid id)
    {
        var scan =
            context.Scans.AsNoTracking().SingleOrDefault(s => s.Id == id)
            ?? throw new KeyNotFoundException("Scan does not exist.");
        if (!Enum.IsDefined(scan.State))
            throw new InvalidDataException("Invalid stored scan state.");
        return new(
            id,
            new(
                scan.VolumeKey,
                scan.Epoch,
                scan.Serial,
                new(checked((ulong)scan.RangeStart), checked((ulong)scan.RangeCount)),
                scan.Started
            ),
            scan.State,
            scan.Ended
        );
    }

    // State validation, writes and publication are enclosed in one transaction.
    // Tracked entities are always released, even after rollback.
    private void Write(Action action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var transaction = context.Database.BeginTransaction();
        try
        {
            action();
            token.ThrowIfCancellationRequested();
            context.SaveChanges();
            token.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        finally
        {
            context.ChangeTracker.Clear();
        }
    }

    public void StageBatch(
        Guid scanId,
        IReadOnlyList<StagedObservation> observations,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count > MaximumBatchSize)
            throw new ArgumentOutOfRangeException(nameof(observations));
        Write(
            () =>
            {
                var scan = RequireRunning(scanId);
                var indices = new HashSet<ulong>();
                foreach (var item in observations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateObservation(item, scan);
                    if (!indices.Add(item.RecordIndex))
                        throw new InvalidDataException(
                            "Batch contains a duplicate record identity."
                        );
                    context.Observations.Add(ToEntity(scanId, item));
                }
            },
            cancellationToken
        );
    }

    private static void ValidateObservation(StagedObservation item, StoredScan scan)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (
            item.RecordIndex < scan.Descriptor.Range.StartIndex
            || item.RecordIndex >= scan.Descriptor.Range.EndExclusive
            || !Enum.IsDefined(item.Kind)
            || !Enum.IsDefined(item.Status)
        )
            throw new ArgumentException(
                "Observation lies outside the scan or has an invalid state.",
                nameof(item)
            );
        var file = item.File;
        if (file is null)
        {
            if (
                item.Kind == MftScanEntryKind.ResolvedFile
                || (
                    item.Kind == MftScanEntryKind.Unresolved
                    && item.Status == LiveFileReadStatus.ObservedUnchanged
                )
                || (
                    item.Kind == MftScanEntryKind.ExtensionRecord
                    && item.Status != LiveFileReadStatus.ObservedUnchanged
                )
            )
                throw new InvalidDataException("Staged kind and acquisition state disagree.");
            return;
        }
        if (
            file.Reference.RecordIndex != item.RecordIndex
            || !Enum.IsDefined(file.Domain)
            || !Enum.IsDefined(file.Resolution)
            || file.Names.IsDefault
            || file.Streams.IsDefault
        )
            throw new InvalidDataException("Invalid staged file observation.");
        if (
            item.Kind == MftScanEntryKind.ExtensionRecord
            || (
                item.Kind == MftScanEntryKind.ResolvedFile
                && (
                    item.Status != LiveFileReadStatus.ObservedUnchanged
                    || file.Resolution != CapacityResolution.Resolved
                )
            )
            || (
                item.Kind == MftScanEntryKind.Unresolved
                && (
                    item.Status == LiveFileReadStatus.ObservedUnchanged
                    || file.Resolution != CapacityResolution.Unresolved
                )
            )
            || (file.Resolution == CapacityResolution.Unresolved && !file.Streams.IsEmpty)
        )
            throw new InvalidDataException("Staged kind, state and capacity disagree.");
        foreach (var name in file.Names)
            if (name is null || string.IsNullOrEmpty(name.Name) || !Enum.IsDefined(name.Namespace))
                throw new InvalidDataException("Invalid staged name.");
    }

    private static ObservationEntity ToEntity(Guid scanId, StagedObservation item)
    {
        var file = item.File;
        return new()
        {
            ScanId = scanId,
            RecordIndex = checked((long)item.RecordIndex),
            Kind = item.Kind,
            Status = item.Status,
            Sequence = file?.Reference.SequenceNumber,
            IsDirectory = file?.IsDirectory,
            Domain = file?.Domain,
            Resolution = file?.Resolution,
            LinksComplete = file?.LinkSetComplete,
            ObservedLinkCount = file?.ObservedHardLinkCount,
            Names = file is null
                ? []
                :
                [
                    .. file.Names.Select(
                        (n, i) =>
                            new FileNameEntity
                            {
                                ScanId = scanId,
                                RecordIndex = checked((long)item.RecordIndex),
                                Ordinal = i,
                                ParentIndex = checked((long)n.Parent.RecordIndex),
                                ParentSequence = n.Parent.SequenceNumber,
                                Name = n.Name,
                                Namespace = n.Namespace,
                                Attributes = n.FileAttributes,
                            }
                    ),
                ],
            Streams = file is null
                ? []
                :
                [
                    .. file.Streams.Select(
                        (s, i) =>
                            new StreamEntity
                            {
                                ScanId = scanId,
                                RecordIndex = checked((long)item.RecordIndex),
                                Ordinal = i,
                                Name = s.Name,
                                Logical = s.LogicalBytes,
                                Allocated = s.AllocatedBytes,
                            }
                    ),
                ],
        };
    }

    public IEnumerable<FileCapacityObservation> ReadFiles(
        Guid scanId,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = GetScan(scanId);
        long after = -1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = context
                .Observations.AsNoTracking()
                .Where(o => o.ScanId == scanId && o.Sequence != null && o.RecordIndex > after)
                .OrderBy(o => o.RecordIndex)
                .Take(MaximumBatchSize)
                .Include(o => o.Names)
                .Include(o => o.Streams)
                .AsSplitQuery()
                .ToArray();
            if (page.Length == 0)
                yield break;
            foreach (var row in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    row.Domain is not { } domain
                    || !Enum.IsDefined(domain)
                    || row.Resolution is not { } resolution
                    || !Enum.IsDefined(resolution)
                    || row.IsDirectory is not { } directory
                    || row.LinksComplete is not { } complete
                    || (resolution == CapacityResolution.Unresolved && row.Streams.Count != 0)
                )
                    throw new InvalidDataException("Stored file state or capacity is invalid.");
                yield return new(
                    FileReference.Create(checked((ulong)row.RecordIndex), row.Sequence!.Value),
                    directory,
                    domain,
                    resolution,
                    [.. row.Names.OrderBy(n => n.Ordinal).Select(ToName)],
                    [
                        .. row
                            .Streams.OrderBy(s => s.Ordinal)
                            .Select(s => new StreamCapacity(s.Name, s.Logical, s.Allocated)),
                    ],
                    complete
                )
                {
                    ObservedHardLinkCount = row.ObservedLinkCount,
                };
            }
            after = page[^1].RecordIndex;
        }
    }

    private static NtfsFileName ToName(FileNameEntity name) =>
        new(
            FileReference.Create(checked((ulong)name.ParentIndex), name.ParentSequence),
            name.Name,
            name.Namespace,
            name.Attributes
        );

    public FolderAggregationResult AggregateStage(
        Guid scanId,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireRunning(scanId);
        var folders = ReadFiles(scanId, cancellationToken)
            .Where(f => f.IsDirectory && f.LinkSetComplete)
            .Select(f => new FolderObservation(
                f.Reference,
                f.Names.Where(n => n.Namespace != FileNameNamespace.Dos)
                    .Select(n => n.Parent)
                    .Distinct()
                    .ToImmutableArray()
            ))
            .ToArray();
        var root =
            folders.SingleOrDefault(f => f.Reference.RecordIndex == 5)
            ?? throw new InvalidDataException(
                "A complete root observation is required to aggregate a scan."
            );
        var graph = FolderGraph.Build(root.Reference, folders, cancellationToken);
        var resolver = new ScanDomainResolver(graph);
        CapacityAmount observed = default;
        var result = new FolderAggregator(graph).Aggregate(
            ReadFiles(scanId, cancellationToken)
                .Select(f =>
                {
                    foreach (var stream in f.Streams)
                        observed += new CapacityAmount(
                            checked((UInt128)stream.LogicalBytes),
                            checked((UInt128)stream.AllocatedBytes)
                        );
                    return resolver.Resolve(f, cancellationToken);
                }),
            cancellationToken
        );
        CapacityAmount accounted = result.Metadata.KnownData + result.Unattributed.KnownData;
        if (result.Folders.TryGetValue(root.Reference, out var rootSample))
            accounted += rootSample.SubtreeData;
        if (accounted != observed)
            throw new InvalidDataException(
                "Folder aggregation did not conserve observed DATA capacity."
            );
        cancellationToken.ThrowIfCancellationRequested();
        return result with
        {
            HasUnknownAttribution =
                result.HasUnknownAttribution
                || context.Observations.Any(o =>
                    o.ScanId == scanId
                    && o.Kind == MftScanEntryKind.Unresolved
                    && o.Sequence == null
                ),
        };
    }

    public void StopScan(
        Guid id,
        StoredScanState state,
        DateTimeOffset endedUtc,
        CancellationToken cancellationToken = default
    )
    {
        if (
            state
            is not (
                StoredScanState.Cancelled
                or StoredScanState.Failed
                or StoredScanState.Interrupted
            )
        )
            throw new ArgumentOutOfRangeException(nameof(state));
        Write(
            () =>
            {
                var scan = RequireRunning(id);
                ValidateEnd(scan, endedUtc);
                var entity = context.Scans.Single(s => s.Id == id);
                entity.State = state;
                entity.Ended = endedUtc;
            },
            cancellationToken
        );
    }

    public Guid? GetPublishedScan(string volumeKey, MftScanRange range)
    {
        long start = checked((long)range.StartIndex),
            count = checked((long)range.RecordCount);
        return context
            .Published.AsNoTracking()
            .Where(p => p.VolumeKey == volumeKey && p.RangeStart == start && p.RangeCount == count)
            .Select(p => (Guid?)p.ScanId)
            .SingleOrDefault();
    }

    private StoredScan RequireRunning(Guid id)
    {
        var scan = GetScan(id);
        if (scan.State != StoredScanState.Running)
            throw new InvalidOperationException("Scan is no longer running.");
        return scan;
    }

    private static void ValidateEnd(StoredScan scan, DateTimeOffset endedUtc)
    {
        if (endedUtc.Offset != TimeSpan.Zero || endedUtc < scan.Descriptor.StartedUtc)
            throw new ArgumentException(
                "End must be UTC and at or after the start.",
                nameof(endedUtc)
            );
    }

    public void Dispose() => context.Dispose();
}
