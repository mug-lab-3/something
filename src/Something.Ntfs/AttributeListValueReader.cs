namespace Something.Ntfs;

/// <summary>Optional acquisition capability. Results must remain unchanged until the next call.</summary>
public interface IAttributeListValueSource
{
    ValueTask<ReadOnlyMemory<byte>> ReadAttributeListAsync(
        NonResidentAttribute attribute,
        CancellationToken cancellationToken
    );
}

/// <summary>Reads complete physical clusters without writes; failures must throw.</summary>
public interface IVolumeClusterSource
{
    ValueTask<ReadOnlyMemory<byte>> ReadClustersAsync(
        long startLcn,
        int clusterCount,
        CancellationToken cancellationToken
    );
}

public enum AttributeListReadFailure
{
    Invalid,
    Unsupported,
    LimitExceeded,
}

public sealed class AttributeListReadException(AttributeListReadFailure failure, string message)
    : Exception(message)
{
    public AttributeListReadFailure Failure { get; } = failure;
}

/// <summary>Bounded acquisition of one complete, uncompressed, nonsparse ATTRIBUTE_LIST extent.</summary>
public sealed class AttributeListValueReader
{
    public const int MaximumValueBytes = 1024 * 1024;
    private readonly int clusterSize;
    private readonly long totalClusters;

    public AttributeListValueReader(int bytesPerCluster, long totalClusters)
    {
        if (
            bytesPerCluster is < 512 or > 2 * 1024 * 1024
            || !System.Numerics.BitOperations.IsPow2((uint)bytesPerCluster)
        )
            throw new ArgumentOutOfRangeException(nameof(bytesPerCluster));
        if (totalClusters <= 0 || totalClusters > long.MaxValue / bytesPerCluster)
            throw new ArgumentOutOfRangeException(nameof(totalClusters));
        clusterSize = bytesPerCluster;
        this.totalClusters = totalClusters;
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(
        IVolumeClusterSource source,
        NonResidentAttribute attribute,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(attribute);
        cancellationToken.ThrowIfCancellationRequested();
        if (
            attribute.Type != NtfsAttributeTypes.AttributeList
            || attribute.Name.Length != 0
            || attribute.Flags != 0
            || attribute.CompressionUnit != 0
            || attribute.LowestVcn != 0
            || attribute.Sizes is not { } sizes
        )
            throw Failure(
                AttributeListReadFailure.Unsupported,
                "Only a complete unnamed, uncompressed ATTRIBUTE_LIST is supported."
            );
        if (
            sizes.LogicalBytes <= 0
            || sizes.InitializedBytes != sizes.LogicalBytes
            || sizes.AllocatedBytes <= 0
        )
            throw Failure(
                AttributeListReadFailure.Invalid,
                "ATTRIBUTE_LIST sizes are invalid or not fully initialized."
            );
        if (sizes.LogicalBytes > MaximumValueBytes)
            throw Failure(
                AttributeListReadFailure.LimitExceeded,
                "ATTRIBUTE_LIST value exceeds the acquisition limit."
            );
        long requiredClusters = (sizes.LogicalBytes + clusterSize - 1) / clusterSize;
        long maximumMapped = ((long)MaximumValueBytes + clusterSize - 1) / clusterSize;
        long nextVcn = 0;
        List<(long Start, long End)> physical = [];
        // Validate the entire plan before allocation or any disk I/O, including unused padding.
        foreach (var run in attribute.Runs)
        {
            if (
                run.StartVcn != nextVcn
                || run.ClusterCount <= 0
                || run.StartLcn is not { } lcn
                || lcn < 0
                || lcn >= totalClusters
                || run.ClusterCount > totalClusters - lcn
            )
                throw Failure(
                    AttributeListReadFailure.Invalid,
                    "ATTRIBUTE_LIST has missing, sparse, or out-of-volume mapping pairs."
                );
            if (run.ClusterCount > maximumMapped - nextVcn)
                throw Failure(
                    AttributeListReadFailure.LimitExceeded,
                    "ATTRIBUTE_LIST mapped extent exceeds the acquisition limit."
                );
            nextVcn += run.ClusterCount;
            physical.Add((lcn, lcn + run.ClusterCount));
        }
        if (
            nextVcn < requiredClusters
            || attribute.HighestVcn != nextVcn - 1
            || sizes.AllocatedBytes != nextVcn * clusterSize
            || sizes.PhysicalAllocatedBytes is not null
        )
            throw Failure(
                AttributeListReadFailure.Invalid,
                "Complete ATTRIBUTE_LIST mapping pairs and sizes disagree."
            );
        var ordered = physical.OrderBy(r => r.Start).ToArray();
        for (int i = 1; i < ordered.Length; i++)
            if (ordered[i].Start < ordered[i - 1].End)
                throw Failure(
                    AttributeListReadFailure.Invalid,
                    "ATTRIBUTE_LIST physical extents overlap."
                );

        byte[] value = new byte[(int)sizes.LogicalBytes];
        int copied = 0;
        long remainingClusters = requiredClusters;
        int maximumChunk = Math.Max(1, 65536 / clusterSize);
        foreach (var run in attribute.Runs)
        {
            long used = 0;
            while (used < run.ClusterCount && remainingClusters > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)
                    Math.Min(maximumChunk, Math.Min(run.ClusterCount - used, remainingClusters));
                var bytes = await source
                    .ReadClustersAsync(run.StartLcn!.Value + used, count, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                int expected = checked(count * clusterSize);
                if (bytes.Length != expected)
                    throw new IOException("Partial or oversized ATTRIBUTE_LIST cluster read.");
                int take = Math.Min(expected, value.Length - copied);
                bytes.Span[..take].CopyTo(value.AsSpan(copied));
                copied += take;
                used += count;
                remainingClusters -= count;
            }
        }
        return value;
    }

    private static AttributeListReadException Failure(
        AttributeListReadFailure failure,
        string message
    ) => new(failure, message);
}
