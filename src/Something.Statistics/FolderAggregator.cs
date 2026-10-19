using System.Collections.Frozen;
using System.Collections.Immutable;
using Something.Ntfs;

namespace Something.Statistics;

/// <summary>Unnamed DATA per visible name. This view intentionally counts hard links repeatedly.</summary>
public sealed record FolderEntrySample(
    CapacityAmount DirectData,
    CapacityAmount SubtreeData,
    ulong DirectFileCount,
    ulong SubtreeFileCount,
    ulong DirectUnresolvedCount,
    ulong SubtreeUnresolvedCount
);

public sealed record FolderCapacitySample(
    FileReference Reference,
    CapacityAmount DirectData,
    CapacityAmount SharedData,
    CapacityAmount DirectoryData,
    CapacityAmount SubtreeData,
    ulong AssignedFileCount,
    ulong SubtreeFileCount,
    ulong DirectUnresolvedCount,
    ulong SubtreeUnresolvedCount,
    bool DirectoryCapacityObserved,
    ulong SubtreeUnobservedDirectoryCount
)
{
    /// <summary>Null for historical generations that predate per-name accounting.</summary>
    public FolderEntrySample? Entries { get; init; }
}

public sealed record CapacityBucket(
    CapacityAmount KnownData,
    ulong ResolvedCount,
    ulong UnresolvedCount
);

/// <summary>Known DATA totals only. Missing observations and unresolved capacity remain separate from known zero.</summary>
public sealed record FolderAggregationResult(
    FrozenDictionary<FileReference, FolderCapacitySample> Folders,
    CapacityBucket Metadata,
    CapacityBucket Unattributed,
    bool HasUnknownAttribution
);

/// <summary>Consumes compact file facts once; retains folder accumulators and identity guards, not stream/run models.</summary>
public sealed class FolderAggregator(FolderGraph graph)
{
    private readonly FolderGraph graph = graph ?? throw new ArgumentNullException(nameof(graph));

    public FolderAggregationResult Aggregate(
        IEnumerable<FileCapacityObservation> observations,
        CancellationToken cancellationToken = default,
        Action<FileCapacityObservation, FileAttribution>? onUnattributed = null,
        Action<int, int>? onRollup = null
    )
    {
        ArgumentNullException.ThrowIfNull(observations);
        cancellationToken.ThrowIfCancellationRequested();
        var policy = new FileAttributionPolicy(graph);
        var folders = graph.ParentFirst.ToDictionary(
            r => r,
            r => new FolderCapacitySample(
                r,
                default,
                default,
                default,
                default,
                0,
                0,
                0,
                0,
                false,
                0
            )
            {
                Entries = new(default, default, 0, 0, 0, 0),
            }
        );
        var seen = new HashSet<ulong>();
        var metadata = new CapacityBucket(default, 0, 0);
        var unattributed = new CapacityBucket(default, 0, 0);
        bool unknownAttribution =
            !graph.Nodes.TryGetValue(graph.Root, out var root)
            || root.Depth is null
            || graph.Nodes.Values.Any(n => n.Issue != FolderIssue.None);
        foreach (var observation in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(observation);
            if (!seen.Add(observation.Reference.RecordIndex))
                throw new InvalidDataException("Duplicate file record identity in one generation.");
            var attribution = policy.Attribute(observation, cancellationToken);
            if (
                observation.IsDirectory
                && folders.TryGetValue(observation.Reference, out var directory)
            )
                folders[observation.Reference] = directory with
                {
                    DirectoryCapacityObserved = true,
                };
            if (attribution.Kind == AttributionKind.Metadata)
            {
                metadata = Add(metadata, attribution.Capacity);
                continue;
            }
            if (attribution.Folder is not { } owner)
            {
                onUnattributed?.Invoke(observation, attribution);
                unattributed = Add(unattributed, attribution.Capacity);
                unknownAttribution = true;
                continue;
            }
            if (!observation.IsDirectory)
            {
                var unnamed = observation.Streams.FirstOrDefault(s => s.Name.Length == 0);
                foreach (var name in observation.Names)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (name.Namespace == FileNameNamespace.Dos)
                        continue;
                    var parent = folders[name.Parent];
                    var entries = parent.Entries!;
                    folders[name.Parent] = parent with
                    {
                        Entries = attribution.Capacity is null
                            ? entries with
                            {
                                DirectUnresolvedCount = checked(entries.DirectUnresolvedCount + 1),
                            }
                            : entries with
                            {
                                DirectData =
                                    entries.DirectData
                                    + (
                                        unnamed is null
                                            ? default
                                            : new CapacityAmount(
                                                (UInt128)unnamed.LogicalBytes,
                                                (UInt128)unnamed.AllocatedBytes
                                            )
                                    ),
                                DirectFileCount = checked(entries.DirectFileCount + 1),
                            },
                    };
                }
            }
            var sample = folders[owner];
            if (attribution.Capacity is not { } capacity)
            {
                folders[owner] = sample with
                {
                    DirectUnresolvedCount = checked(sample.DirectUnresolvedCount + 1),
                };
                continue;
            }
            folders[owner] = sample with
            {
                DirectData =
                    attribution.Kind == AttributionKind.Direct
                        ? sample.DirectData + capacity
                        : sample.DirectData,
                SharedData =
                    attribution.Kind == AttributionKind.Shared
                        ? sample.SharedData + capacity
                        : sample.SharedData,
                DirectoryData =
                    attribution.Kind == AttributionKind.Directory
                        ? sample.DirectoryData + capacity
                        : sample.DirectoryData,
                AssignedFileCount = checked(
                    sample.AssignedFileCount + (observation.IsDirectory ? 0UL : 1UL)
                ),
            };
        }
        onRollup?.Invoke(0, graph.ParentFirst.Length);
        for (int i = graph.ParentFirst.Length - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int completed = graph.ParentFirst.Length - i - 1;
            if (completed > 0 && completed % 1024 == 0)
                onRollup?.Invoke(completed, graph.ParentFirst.Length);
            var reference = graph.ParentFirst[i];
            var sample = folders[reference];
            sample = sample with
            {
                SubtreeData =
                    sample.SubtreeData
                    + sample.DirectData
                    + sample.SharedData
                    + sample.DirectoryData,
                SubtreeFileCount = checked(sample.SubtreeFileCount + sample.AssignedFileCount),
                SubtreeUnresolvedCount = checked(
                    sample.SubtreeUnresolvedCount + sample.DirectUnresolvedCount
                ),
                SubtreeUnobservedDirectoryCount = checked(
                    sample.SubtreeUnobservedDirectoryCount
                    + (sample.DirectoryCapacityObserved ? 0UL : 1UL)
                ),
                Entries = sample.Entries! with
                {
                    SubtreeData = sample.Entries.SubtreeData + sample.Entries.DirectData,
                    SubtreeFileCount = checked(
                        sample.Entries.SubtreeFileCount + sample.Entries.DirectFileCount
                    ),
                    SubtreeUnresolvedCount = checked(
                        sample.Entries.SubtreeUnresolvedCount + sample.Entries.DirectUnresolvedCount
                    ),
                },
            };
            folders[reference] = sample;
            if (reference == graph.Root)
                continue;
            var parent = graph.Nodes[reference].Parent!.Value;
            var parentSample = folders[parent];
            folders[parent] = parentSample with
            {
                SubtreeData = parentSample.SubtreeData + sample.SubtreeData,
                SubtreeFileCount = checked(parentSample.SubtreeFileCount + sample.SubtreeFileCount),
                SubtreeUnresolvedCount = checked(
                    parentSample.SubtreeUnresolvedCount + sample.SubtreeUnresolvedCount
                ),
                SubtreeUnobservedDirectoryCount = checked(
                    parentSample.SubtreeUnobservedDirectoryCount
                    + sample.SubtreeUnobservedDirectoryCount
                ),
                Entries = parentSample.Entries! with
                {
                    SubtreeData = parentSample.Entries.SubtreeData + sample.Entries!.SubtreeData,
                    SubtreeFileCount = checked(
                        parentSample.Entries.SubtreeFileCount + sample.Entries.SubtreeFileCount
                    ),
                    SubtreeUnresolvedCount = checked(
                        parentSample.Entries.SubtreeUnresolvedCount
                        + sample.Entries.SubtreeUnresolvedCount
                    ),
                },
            };
        }
        onRollup?.Invoke(graph.ParentFirst.Length, graph.ParentFirst.Length);
        return new(folders.ToFrozenDictionary(), metadata, unattributed, unknownAttribution);
    }

    private static CapacityBucket Add(CapacityBucket bucket, CapacityAmount? capacity) =>
        capacity is { } known
            ? bucket with
            {
                KnownData = bucket.KnownData + known,
                ResolvedCount = checked(bucket.ResolvedCount + 1),
            }
            : bucket with
            {
                UnresolvedCount = checked(bucket.UnresolvedCount + 1),
            };
}
