using System.Collections.Immutable;
using System.Numerics;
using Microsoft.EntityFrameworkCore;
using Something.Ntfs;

namespace Something.Storage;

public enum FolderChangePeriod
{
    Last24Hours,
    Last7Days,
    Last365Days,
    Last30Days,
    Today,
    ThisWeek,
    ThisMonth,
    ThisYear,
    Custom,
}

public enum FolderChangeRanking
{
    Growth,
    Shrinkage,
    Magnitude,
}

public enum FolderComparisonAvailability
{
    Available,
    NoLatestObservation,
    NoBaseline,
    NoRecentObservation,
}

public enum FolderChangeStatus
{
    Comparable,
    UncertainCapacity,
    NotObservedBefore,
    NotObservedAfter,
}

/// <summary>Allocated unnamed DATA per visible name, including descendants. Rows overlap.
/// Identity matching uses volume plus MFT index and sequence; it is optimistic, not proof of continuous identity.</summary>
public sealed record FolderChange(
    FileReference Reference,
    string? BeforePath,
    string? AfterPath,
    UInt128? BeforeAllocatedBytes,
    UInt128? AfterAllocatedBytes,
    BigInteger? AllocatedDelta,
    FolderChangeStatus Status
);

public sealed record FolderChangeReport(
    string VolumeKey,
    FolderChangePeriod Period,
    DateTimeOffset AsOfUtc,
    DateTimeOffset RequestedBaselineUtc,
    ScanHistoryEntry? Baseline,
    ScanHistoryEntry? Latest,
    FolderComparisonAvailability Availability,
    ImmutableArray<FolderChange> Folders
)
{
    public FolderChange? Root { get; init; }
    public ImmutableArray<FolderChange> ChangedFolders =>
        [
            .. Folders.Where(row =>
                row.Status == FolderChangeStatus.Comparable && row.AllocatedDelta != BigInteger.Zero
            ),
        ];

    public ImmutableArray<FolderChange> UncertainFolders =>
        [.. Folders.Where(row => row.Status != FolderChangeStatus.Comparable)];

    public ImmutableArray<FolderChange> Rank(FolderChangeRanking ranking, int count = 20)
    {
        if (!Enum.IsDefined(ranking))
            throw new ArgumentOutOfRangeException(nameof(ranking));
        if (count is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(count));
        var rows = Folders.Where(row => row.AllocatedDelta is not null);
        var ordered = ranking switch
        {
            FolderChangeRanking.Growth => rows.Where(row => row.AllocatedDelta > 0)
                .OrderByDescending(row => row.AllocatedDelta),
            FolderChangeRanking.Shrinkage => rows.Where(row => row.AllocatedDelta < 0)
                .OrderBy(row => row.AllocatedDelta),
            _ => rows.Where(row => row.AllocatedDelta != 0)
                .OrderByDescending(row => BigInteger.Abs(row.AllocatedDelta!.Value)),
        };
        return
        [
            .. ordered
                .ThenBy(row => row.Reference.RecordIndex)
                .ThenBy(row => row.Reference.SequenceNumber)
                .Take(count),
        ];
    }
}

public sealed partial class ScanHistoryArchive
{
    public FolderChangeReport ReadFolderChanges(
        string volumeKey,
        Guid beforeId,
        Guid afterId,
        CancellationToken token = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeKey);
        if (beforeId == afterId)
            throw new ArgumentException("Select two distinct scans.", nameof(afterId));
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            using var store = OpenDatabase(token);
            using var transaction = store.HistoryContext.Database.BeginTransaction();
            var query = store
                .HistoryContext.HistorySnapshots.AsNoTracking()
                .Where(row => row.VolumeKey == volumeKey && !row.Retired);
            var before =
                query.SingleOrDefault(row => row.Id == beforeId)
                ?? throw new InvalidOperationException(
                    "The comparison baseline is no longer available."
                );
            var after =
                query.SingleOrDefault(row => row.Id == afterId)
                ?? throw new InvalidOperationException(
                    "The comparison endpoint is no longer available."
                );
            if (before.CompletedUtcTicks > after.CompletedUtcTicks)
                throw new ArgumentException(
                    "The baseline must precede the endpoint.",
                    nameof(beforeId)
                );
            var changes = CompareFolders(store, beforeId, afterId, token, out var root);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(
                volumeKey,
                FolderChangePeriod.Custom,
                ToEntry(after).CompletedUtc,
                ToEntry(before).CompletedUtc,
                ToEntry(before),
                ToEntry(after),
                FolderComparisonAvailability.Available,
                changes
            )
            {
                Root = root,
            };
        }
    }

    /// <summary>Uses the latest retained completion at or before each requested endpoint.
    /// Missing folders and unresolved capacities are never treated as zero or confirmed deletions.</summary>
    public FolderChangeReport ReadFolderChanges(
        string volumeKey,
        FolderChangePeriod period,
        DateTimeOffset? asOfUtc = null,
        CancellationToken token = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeKey);
        var end = (asOfUtc ?? clock.GetUtcNow()).ToUniversalTime();
        var start = ComparisonSelection.Resolve([], period, end, timeZone).RequestedBeforeUtc;
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            using var store = OpenDatabase(token);
            // One short read transaction keeps endpoint selection and all facts coherent
            // if a different application instance publishes or prunes history concurrently.
            using var transaction = store.HistoryContext.Database.BeginTransaction();
            var query = store
                .HistoryContext.HistorySnapshots.AsNoTracking()
                .Where(row => row.VolumeKey == volumeKey && !row.Retired);
            var latest = query
                .Where(row => row.CompletedUtcTicks <= end.UtcTicks)
                .OrderByDescending(row => row.CompletedUtcTicks)
                .ThenByDescending(row => row.Id)
                .FirstOrDefault();
            var baseline = query
                .Where(row => row.CompletedUtcTicks <= start.UtcTicks)
                .OrderByDescending(row => row.CompletedUtcTicks)
                .ThenByDescending(row => row.Id)
                .FirstOrDefault();
            var availability =
                latest is null ? FolderComparisonAvailability.NoLatestObservation
                : latest.CompletedUtcTicks <= start.UtcTicks
                    ? FolderComparisonAvailability.NoRecentObservation
                : baseline is null ? FolderComparisonAvailability.NoBaseline
                : FolderComparisonAvailability.Available;
            ImmutableArray<FolderChange> changes = [];
            FolderChange? root = null;
            if (availability == FolderComparisonAvailability.Available)
                changes = CompareFolders(store, baseline!.Id, latest!.Id, token, out root);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(
                volumeKey,
                period,
                end,
                start,
                baseline is null ? null : ToEntry(baseline),
                latest is null ? null : ToEntry(latest),
                availability,
                changes
            )
            {
                Root = root,
            };
        }
    }

    private static ImmutableArray<FolderChange> CompareFolders(
        ScanStore store,
        Guid beforeId,
        Guid afterId,
        CancellationToken token,
        out FolderChange? root
    )
    {
        var before = ReadComparisonFolders(store, beforeId, token);
        var after = ReadComparisonFolders(store, afterId, token);
        var rows = ImmutableArray.CreateBuilder<FolderChange>();
        foreach (
            var reference in before
                .Keys.Union(after.Keys)
                .OrderBy(r => r.RecordIndex)
                .ThenBy(r => r.SequenceNumber)
        )
        {
            token.ThrowIfCancellationRequested();
            before.TryGetValue(reference, out var old);
            after.TryGetValue(reference, out var current);
            var status =
                old is null ? FolderChangeStatus.NotObservedBefore
                : current is null ? FolderChangeStatus.NotObservedAfter
                : old.Bytes is null || current.Bytes is null ? FolderChangeStatus.UncertainCapacity
                : FolderChangeStatus.Comparable;
            BigInteger? delta =
                status == FolderChangeStatus.Comparable
                    ? BigInteger.CreateChecked(current!.Bytes!.Value)
                        - BigInteger.CreateChecked(old!.Bytes!.Value)
                    : null;
            rows.Add(
                new(reference, old?.Path, current?.Path, old?.Bytes, current?.Bytes, delta, status)
            );
        }
        root =
            rows.FirstOrDefault(row => row.Reference.RecordIndex == 5 && row.AfterPath is not null)
            ?? rows.FirstOrDefault(row => row.Reference.RecordIndex == 5);
        return [.. rows.Where(row => row.Reference.RecordIndex != 5)];
    }

    private sealed record ComparisonFolder(string Path, UInt128? Bytes);

    private static Dictionary<FileReference, ComparisonFolder> ReadComparisonFolders(
        ScanStore store,
        Guid id,
        CancellationToken token
    )
    {
        var aggregation = store.ReadAggregation(id, token);
        // Legacy generations lacking the per-name policy must not be compared using another metric.
        if (aggregation.Folders.Values.Any(folder => folder.Entries is null))
            throw new InvalidOperationException(
                "History predates per-name accounting and cannot be compared. Rescan to establish a baseline."
            );
        bool unknownAttribution =
            aggregation.HasUnknownAttribution || aggregation.Unattributed.UnresolvedCount != 0;
        var folders = store
            .ReadRankedFolders(id, int.MaxValue, token)
            .ToDictionary(
                row => row.Sample.Reference,
                row => new ComparisonFolder(
                    row.RelativePath,
                    !unknownAttribution
                    && row.Sample.DirectoryCapacityObserved
                    && row.Sample.SubtreeUnobservedDirectoryCount == 0
                    && row.Sample.Entries!.SubtreeUnresolvedCount == 0
                        ? row.Data.AllocatedBytes
                        : null
                )
            );
        var root = aggregation.Folders.Single(row => row.Key.RecordIndex == 5).Value;
        folders.Add(
            root.Reference,
            new(
                "",
                !unknownAttribution
                && root.DirectoryCapacityObserved
                && root.SubtreeUnobservedDirectoryCount == 0
                && root.Entries!.SubtreeUnresolvedCount == 0
                    ? root.Entries.SubtreeData.AllocatedBytes
                    : null
            )
        );
        return folders;
    }
}
