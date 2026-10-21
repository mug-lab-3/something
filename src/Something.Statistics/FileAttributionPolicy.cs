using System.Collections.Immutable;
using Something.Ntfs;

namespace Something.Statistics;

public readonly record struct CapacityAmount(UInt128 LogicalBytes, UInt128 AllocatedBytes)
{
    public static CapacityAmount operator +(CapacityAmount left, CapacityAmount right) =>
        new(
            checked(left.LogicalBytes + right.LogicalBytes),
            checked(left.AllocatedBytes + right.AllocatedBytes)
        );
}

public sealed record StreamCapacity
{
    public string Name { get; }
    public long LogicalBytes { get; }
    public long AllocatedBytes { get; }

    public StreamCapacity(string name, long logicalBytes, long allocatedBytes)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfNegative(logicalBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(allocatedBytes);
        Name = name;
        LogicalBytes = logicalBytes;
        AllocatedBytes = allocatedBytes;
    }
}

public enum CapacityDomain
{
    Ordinary,
    NtfsMetadata,
    Unclassified,
}

public enum CapacityResolution
{
    Resolved,
    Unresolved,
}

public enum AttributionKind
{
    Direct,
    Shared,
    Directory,
    Metadata,
    Unattributed,
}

public enum AttributionIssue
{
    None,
    IncompleteLinks,
    InvalidNames,
    InvalidHierarchy,
    UnknownDomain,
}

/// <summary>Compact facts for one generation. LinkSetComplete is evidence supplied by acquisition, not inferred from available names.</summary>
public sealed record FileCapacityObservation(
    FileReference Reference,
    bool IsDirectory,
    CapacityDomain Domain,
    CapacityResolution Resolution,
    ImmutableArray<NtfsFileName> Names,
    ImmutableArray<StreamCapacity> Streams,
    bool LinkSetComplete
)
{
    public ushort? ObservedHardLinkCount { get; init; }
    public uint? StandardFileAttributes { get; init; }
    public bool? IsHidden =>
        StandardFileAttributes is { } attributes
            ? ((FileAttributes)attributes).HasFlag(FileAttributes.Hidden)
            : null;
    public bool? IsSystem =>
        StandardFileAttributes is { } attributes
            ? ((FileAttributes)attributes).HasFlag(FileAttributes.System)
            : null;
}

public sealed record FileAttribution(
    FileReference Reference,
    AttributionKind Kind,
    FileReference? Folder,
    CapacityAmount? Capacity,
    AttributionIssue Issue
);

/// <summary>Each DATA capacity receives one owner. Alias entries never create another physical file.</summary>
public sealed class FileAttributionPolicy(FolderGraph graph)
{
    private readonly FolderGraph graph = graph ?? throw new ArgumentNullException(nameof(graph));

    public FileAttribution Attribute(
        FileCapacityObservation observation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        if (
            !Enum.IsDefined(observation.Domain)
            || !Enum.IsDefined(observation.Resolution)
            || observation.Names.IsDefault
            || observation.Streams.IsDefault
        )
            throw new ArgumentException(
                "Observation fields must be initialized and have known states.",
                nameof(observation)
            );
        if (observation.Resolution == CapacityResolution.Unresolved && !observation.Streams.IsEmpty)
            throw new ArgumentException(
                "Unresolved observations must not contain partial stream capacities.",
                nameof(observation)
            );
        if (!observation.IsDirectory && graph.Nodes.ContainsKey(observation.Reference))
            throw new InvalidDataException("File kind conflicts with the folder graph.");
        CapacityAmount? capacity = null;
        if (observation.Resolution == CapacityResolution.Resolved)
        {
            var sum = new CapacityAmount();
            var streams = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stream in observation.Streams)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(stream);
                if (!streams.Add(stream.Name))
                    throw new InvalidDataException(
                        "Duplicate DATA stream in one file observation."
                    );
                sum += new CapacityAmount(
                    (UInt128)stream.LogicalBytes,
                    (UInt128)stream.AllocatedBytes
                );
            }
            capacity = sum;
        }
        FileAttribution Result(
            AttributionKind kind,
            FileReference? folder,
            AttributionIssue issue = AttributionIssue.None
        ) => new(observation.Reference, kind, folder, capacity, issue);
        if (observation.Domain == CapacityDomain.NtfsMetadata)
            return Result(AttributionKind.Metadata, null);
        if (observation.Domain == CapacityDomain.Unclassified)
            return Result(AttributionKind.Unattributed, null, AttributionIssue.UnknownDomain);
        if (observation.IsDirectory)
            return
                graph.Nodes.TryGetValue(observation.Reference, out var node)
                && node.Depth is not null
                ? Result(AttributionKind.Directory, observation.Reference)
                : Result(AttributionKind.Unattributed, null, AttributionIssue.InvalidHierarchy);
        if (!observation.LinkSetComplete)
            return Result(AttributionKind.Unattributed, null, AttributionIssue.IncompleteLinks);
        var parents = new HashSet<FileReference>();
        foreach (var name in observation.Names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(name);
            if (!Enum.IsDefined(name.Namespace) || string.IsNullOrEmpty(name.Name))
                return Result(AttributionKind.Unattributed, null, AttributionIssue.InvalidNames);
            if (name.Namespace != FileNameNamespace.Dos)
                parents.Add(name.Parent);
        }
        if (parents.Count == 0)
            return Result(AttributionKind.Unattributed, null, AttributionIssue.IncompleteLinks);
        var ancestor = graph.FindCommonAncestor(parents, cancellationToken);
        return ancestor is { } owner
            ? Result(parents.Count == 1 ? AttributionKind.Direct : AttributionKind.Shared, owner)
            : Result(AttributionKind.Unattributed, null, AttributionIssue.InvalidHierarchy);
    }
}
