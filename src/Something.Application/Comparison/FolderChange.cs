using System.Collections.Immutable;
using System.Numerics;
using Something.Application.History;
using Something.Ntfs;

namespace Something.Application.Comparison;

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
    public bool IsPartial =>
        Availability == FolderComparisonAvailability.Available
        && Baseline is not null
        && Baseline.CompletedUtc > RequestedBaselineUtc;
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
        if (count is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(count));
        return [.. OrderChanges(ranking).Take(count)];
    }

    /// <summary>All known nonzero changes in the requested order, without a top-N limit.</summary>
    public ImmutableArray<FolderChange> Analyze(FolderChangeRanking ranking) =>
        [.. OrderChanges(ranking)];

    private IOrderedEnumerable<FolderChange> OrderChanges(FolderChangeRanking ranking)
    {
        if (!Enum.IsDefined(ranking))
            throw new ArgumentOutOfRangeException(nameof(ranking));
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
        return ordered
            .ThenBy(row => row.Reference.RecordIndex)
            .ThenBy(row => row.Reference.SequenceNumber);
    }
}
