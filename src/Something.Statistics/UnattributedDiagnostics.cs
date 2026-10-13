using System.Collections.Immutable;
using Something.Ntfs;

namespace Something.Statistics;

public enum UnattributedReason
{
    IncompleteLinks,
    DuplicateNames,
    OrphanDosAlias,
    NoVisibleNames,
    MissingParent,
    ReusedParent,
    InvalidHierarchy,
    MixedDomains,
    UnknownDomain,
    InvalidNames,
}

public sealed record UnattributedSample(
    FileReference Reference,
    bool IsDirectory,
    CapacityAmount? Capacity,
    ushort? ObservedNameCount,
    int Names,
    int VisibleNames,
    int DosNames,
    FileReference? FirstParent,
    FileReference? ProblemParent,
    FolderIssue? HierarchyIssue
);

public sealed record UnattributedSummary(
    UnattributedReason Reason,
    CapacityAmount KnownData,
    ulong ResolvedCount,
    ulong UnresolvedCount,
    ImmutableArray<UnattributedSample> Samples
);

/// <summary>Explains exclusions without changing ownership or inferred capacity.
/// Keeps only two largest known examples per reason; never retains paths or raw data.</summary>
public sealed class UnattributedDiagnostics(FolderGraph graph)
{
    private readonly FolderGraph graph = graph ?? throw new ArgumentNullException(nameof(graph));
    private readonly Dictionary<ulong, FileReference> references = graph.Nodes.Keys.ToDictionary(
        r => r.RecordIndex
    );
    private readonly Dictionary<UnattributedReason, UnattributedSummary> groups = [];

    public void Add(FileCapacityObservation observation, FileAttribution attribution)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(attribution);
        if (
            attribution.Kind != AttributionKind.Unattributed
            || attribution.Reference != observation.Reference
        )
            throw new ArgumentException("Diagnostics require a matching unattributed observation.");
        var (reason, issue, problemParent) = Explain(observation, attribution);
        var old = groups.GetValueOrDefault(reason) ?? new(reason, default, 0, 0, []);
        var sample = new UnattributedSample(
            observation.Reference,
            observation.IsDirectory,
            attribution.Capacity,
            observation.ObservedHardLinkCount,
            observation.Names.Length,
            observation.Names.Count(n => n.Namespace != FileNameNamespace.Dos),
            observation.Names.Count(n => n.Namespace == FileNameNamespace.Dos),
            observation.Names.FirstOrDefault(n => n.Namespace != FileNameNamespace.Dos)?.Parent,
            problemParent,
            issue
        );
        groups[reason] = old with
        {
            KnownData = old.KnownData + (attribution.Capacity ?? default),
            ResolvedCount = checked(old.ResolvedCount + (attribution.Capacity is null ? 0UL : 1UL)),
            UnresolvedCount = checked(
                old.UnresolvedCount + (attribution.Capacity is null ? 1UL : 0UL)
            ),
            Samples =
            [
                .. old
                    .Samples.Append(sample)
                    .OrderByDescending(s => s.Capacity.HasValue)
                    .ThenByDescending(s => s.Capacity?.AllocatedBytes ?? 0)
                    .ThenBy(s => s.Reference.RecordIndex)
                    .Take(2),
            ],
        };
    }

    public ImmutableArray<UnattributedSummary> Snapshot() =>
        [.. groups.Values.OrderByDescending(g => g.KnownData.AllocatedBytes).ThenBy(g => g.Reason)];

    private (UnattributedReason, FolderIssue?, FileReference?) Explain(
        FileCapacityObservation file,
        FileAttribution attribution
    )
    {
        if (attribution.Issue == AttributionIssue.InvalidNames)
            return (UnattributedReason.InvalidNames, null, null);
        if (!file.IsDirectory && !file.LinkSetComplete)
        {
            if (!file.Names.Any(n => n.Namespace != FileNameNamespace.Dos))
                return (UnattributedReason.NoVisibleNames, null, null);
            if (
                file.Names.Select(n => (n.Parent, n.Name, n.Namespace)).Distinct().Count()
                != file.Names.Length
            )
                return (UnattributedReason.DuplicateNames, null, null);
            var orphan = file.Names.FirstOrDefault(n =>
                n.Namespace == FileNameNamespace.Dos
                && !file.Names.Any(longName =>
                    longName.Parent == n.Parent && longName.Namespace == FileNameNamespace.Win32
                )
            );
            return orphan is null
                ? (UnattributedReason.IncompleteLinks, null, null)
                : (UnattributedReason.OrphanDosAlias, null, orphan.Parent);
        }
        if (file.IsDirectory)
            return (
                UnattributedReason.InvalidHierarchy,
                graph.Nodes.GetValueOrDefault(file.Reference)?.Issue,
                graph.Nodes.GetValueOrDefault(file.Reference)?.Parent
            );
        var parents = file
            .Names.Where(n => n.Namespace != FileNameNamespace.Dos)
            .Select(n => n.Parent)
            .Distinct()
            .ToArray();
        if (parents.Length == 0)
            return (UnattributedReason.NoVisibleNames, null, null);
        foreach (var parent in parents)
        {
            if (!graph.Nodes.TryGetValue(parent, out var node))
                return references.ContainsKey(parent.RecordIndex)
                    ? (UnattributedReason.ReusedParent, FolderIssue.ReusedParent, parent)
                    : (UnattributedReason.MissingParent, FolderIssue.MissingParent, parent);
            if (node.Depth is null || node.Issue != FolderIssue.None)
                return (UnattributedReason.InvalidHierarchy, node.Issue, parent);
        }
        bool Metadata(FileReference parent)
        {
            while (parent != graph.Root)
            {
                if (parent.RecordIndex < 16)
                    return true;
                parent = graph.Nodes[parent].Parent!.Value;
            }
            return false;
        }
        return parents.Select(Metadata).Distinct().Count() > 1
            ? (UnattributedReason.MixedDomains, null, null)
            : (UnattributedReason.UnknownDomain, null, null);
    }
}
