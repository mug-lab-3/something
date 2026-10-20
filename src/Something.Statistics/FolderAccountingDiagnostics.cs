using System.Collections.Immutable;
using Something.Ntfs;

namespace Something.Statistics;

[Flags]
public enum FileStorageTraits
{
    None = 0,
    CompressedData = 1,
    SparseData = 2,
    ReparsePoint = 4,
    ResidentData = 8,
}

/// <summary>Alternative accounting views of the same optimistic observations.</summary>
public sealed record FolderAccountingSummary(
    CapacityAmount AssignedData,
    CapacityAmount AssignedNamedStreams,
    CapacityAmount AssignedDirectoryData,
    CapacityAmount VisibleUniqueUnnamedData,
    CapacityAmount PerVisibleNameUnnamedData,
    CapacityAmount DuplicateNameExtraUnnamedData,
    CapacityAmount CrossBoundaryUnnamedData,
    ulong VisibleFiles,
    ulong VisibleNames,
    ulong MultipleNameFiles,
    ulong DosAliases,
    ulong DescendantDirectories,
    ulong IncompleteNameSets,
    ulong UnattributedVisibleFiles,
    ulong UnresolvedVisibleFiles,
    ulong ReparseDirectories,
    ImmutableDictionary<FileStorageTraits, CapacityBucket> SpecialFiles
);

public static class FolderAccountingDiagnostics
{
    private static readonly FileStorageTraits[] Categories =
    [
        FileStorageTraits.CompressedData,
        FileStorageTraits.SparseData,
        FileStorageTraits.ReparsePoint,
        FileStorageTraits.ResidentData,
    ];

    public static FileStorageTraits Classify(NtfsFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        FileStorageTraits traits = FileStorageTraits.None;
        foreach (var stream in file.DataStreams)
        {
            if (stream.IsResident)
                traits |= FileStorageTraits.ResidentData;
            var flags = stream.Storage?.Flags ?? 0;
            if ((flags & NtfsAttributeFlags.Compressed) != 0)
                traits |= FileStorageTraits.CompressedData;
            if ((flags & NtfsAttributeFlags.Sparse) != 0)
                traits |= FileStorageTraits.SparseData;
        }
        if (file.Attributes.Any(a => a.Type == NtfsAttributeTypes.ReparsePoint))
            traits |= FileStorageTraits.ReparsePoint;
        return traits;
    }

    public static FolderAccountingSummary Analyze(
        FolderGraph graph,
        FileReference scope,
        IEnumerable<FileCapacityObservation> observations,
        IReadOnlyDictionary<FileReference, FileStorageTraits>? traits = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(observations);
        cancellationToken.ThrowIfCancellationRequested();
        if (!graph.Nodes.TryGetValue(scope, out var scopeNode) || scopeNode.Depth is null)
            throw new ArgumentException("A valid scope folder is required.", nameof(scope));
        var members = new HashSet<FileReference> { scope };
        foreach (var reference in graph.ParentFirst)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (graph.Nodes[reference].Parent is { } parent && members.Contains(parent))
                members.Add(reference);
        }
        var policy = new FileAttributionPolicy(graph);
        var seen = new HashSet<ulong>();
        CapacityAmount assigned = default,
            named = default,
            directoryData = default,
            unique = default,
            perName = default,
            duplicateExtra = default,
            crossBoundary = default,
            assignedUnnamed = default;
        ulong files = 0,
            names = 0,
            multiple = 0,
            dos = 0,
            directories = 0,
            incomplete = 0,
            unknownAttribution = 0,
            unresolved = 0,
            reparseDirectories = 0;
        var special = new Dictionary<FileStorageTraits, CapacityBucket>
        {
            [FileStorageTraits.CompressedData] = new(default, 0, 0),
            [FileStorageTraits.SparseData] = new(default, 0, 0),
            [FileStorageTraits.ReparsePoint] = new(default, 0, 0),
            [FileStorageTraits.ResidentData] = new(default, 0, 0),
        };
        foreach (var file in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(file);
            if (!seen.Add(file.Reference.RecordIndex))
                throw new InvalidDataException("Duplicate diagnostic record identity.");
            var attribution = policy.Attribute(file, cancellationToken);
            if (attribution.Kind == AttributionKind.Metadata)
                continue;
            bool ownerInside = attribution.Folder is { } owner && members.Contains(owner);
            var flags = traits?.GetValueOrDefault(file.Reference) ?? FileStorageTraits.None;
            if (
                (
                    flags
                    & ~(
                        FileStorageTraits.CompressedData
                        | FileStorageTraits.SparseData
                        | FileStorageTraits.ReparsePoint
                        | FileStorageTraits.ResidentData
                    )
                ) != 0
            )
                throw new InvalidDataException("Unknown diagnostic storage traits.");
            if (ownerInside && attribution.Capacity is { } capacity)
                assigned += capacity;
            if (file.IsDirectory)
            {
                if (ownerInside)
                {
                    if (attribution.Capacity is { } data)
                        directoryData += data;
                    if (file.Reference != scope)
                    {
                        directories = checked(directories + 1);
                        if ((flags & FileStorageTraits.ReparsePoint) != 0)
                            reparseDirectories = checked(reparseDirectories + 1);
                    }
                }
                continue;
            }
            ulong visible = 0;
            foreach (var name in file.Names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!members.Contains(name.Parent))
                    continue;
                if (name.Namespace == FileNameNamespace.Dos)
                    dos = checked(dos + 1);
                else
                    visible = checked(visible + 1);
            }
            CapacityAmount unnamed = default,
                namedData = default;
            foreach (var stream in file.Streams)
                if (stream.Name.Length == 0)
                    unnamed += new CapacityAmount(
                        (UInt128)stream.LogicalBytes,
                        (UInt128)stream.AllocatedBytes
                    );
                else
                    namedData += new CapacityAmount(
                        (UInt128)stream.LogicalBytes,
                        (UInt128)stream.AllocatedBytes
                    );
            if (ownerInside)
            {
                assignedUnnamed += unnamed;
                named += namedData;
            }
            if (visible == 0)
                continue;
            files = checked(files + 1);
            names = checked(names + visible);
            if (visible > 1)
                multiple = checked(multiple + 1);
            if (!file.LinkSetComplete)
            {
                incomplete = checked(incomplete + 1);
                continue;
            }
            if (attribution.Kind == AttributionKind.Unattributed)
            {
                unknownAttribution = checked(unknownAttribution + 1);
                continue;
            }
            if (attribution.Capacity is null)
                unresolved = checked(unresolved + 1);
            else
            {
                unique += unnamed;
                var repeated = new CapacityAmount(
                    checked(unnamed.LogicalBytes * visible),
                    checked(unnamed.AllocatedBytes * visible)
                );
                perName += repeated;
                duplicateExtra += new CapacityAmount(
                    checked(unnamed.LogicalBytes * (visible - 1)),
                    checked(unnamed.AllocatedBytes * (visible - 1))
                );
                if (!ownerInside)
                    crossBoundary += unnamed;
            }
            foreach (var kind in Categories)
                if ((flags & kind) != 0)
                {
                    var bucket = special[kind];
                    special[kind] = attribution.Capacity is { } data
                        ? bucket with
                        {
                            KnownData = bucket.KnownData + data,
                            ResolvedCount = checked(bucket.ResolvedCount + 1),
                        }
                        : bucket with
                        {
                            UnresolvedCount = checked(bucket.UnresolvedCount + 1),
                        };
                }
        }
        if (
            unique != assignedUnnamed + crossBoundary
            || perName != unique + duplicateExtra
            || assigned != assignedUnnamed + named + directoryData
        )
            throw new InvalidDataException("Diagnostic accounting identities disagree.");
        return new(
            assigned,
            named,
            directoryData,
            unique,
            perName,
            duplicateExtra,
            crossBoundary,
            files,
            names,
            multiple,
            dos,
            directories,
            incomplete,
            unknownAttribution,
            unresolved,
            reparseDirectories,
            special.ToImmutableDictionary()
        );
    }
}
