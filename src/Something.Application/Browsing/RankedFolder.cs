using Something.Statistics;

namespace Something.Application.Browsing;

/// <summary>Paths are observed names for display, not paths to open or follow.</summary>
public enum FolderRankingMetric
{
    PerVisibleNameUnnamedData,
    SingleCountAllData,
}

/// <summary>Depth is absolute: volume root is zero. A leaf has no observed valid child directories.</summary>
public sealed record FolderRankingFilter(int? Depth = null, bool LeafOnly = false);

public sealed record RankedFolder(string RelativePath, FolderCapacitySample Sample)
{
    public FolderRankingMetric Metric { get; init; }
    public CapacityAmount Data =>
        Metric == FolderRankingMetric.SingleCountAllData
            ? Sample.SubtreeData
            : Sample.Entries?.SubtreeData
                ?? throw new InvalidOperationException(
                    "This generation has no per-name aggregation."
                );
}
