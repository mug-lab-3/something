using System.Collections.Immutable;
using Something.Ntfs;

namespace Something.Statistics;

public sealed record FolderObservation(
    FileReference Reference,
    ImmutableArray<FileReference> Parents
);

public enum FolderIssue
{
    None,
    MissingRoot,
    InvalidRoot,
    MissingParent,
    ReusedParent,
    AmbiguousParent,
    Cycle,
    InvalidAncestor,
}

public sealed record FolderNode(
    FileReference Reference,
    FileReference? Parent,
    int? Depth,
    FolderIssue Issue
);

/// <summary>Validated identity graph. Never follows paths or reparse targets.</summary>
public sealed class FolderGraph
{
    public FileReference Root { get; }
    public ImmutableDictionary<FileReference, FolderNode> Nodes { get; }
    internal ImmutableArray<FileReference> ParentFirst { get; }

    private FolderGraph(
        FileReference root,
        Dictionary<FileReference, FolderNode> nodes,
        List<FileReference> order
    )
    {
        Root = root;
        Nodes = nodes.ToImmutableDictionary();
        ParentFirst = order.ToImmutableArray();
    }

    public static FolderGraph Build(
        FileReference root,
        IEnumerable<FolderObservation> observations,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(observations);
        cancellationToken.ThrowIfCancellationRequested();
        var nodes = new Dictionary<FileReference, FolderNode>();
        var indices = new HashSet<ulong>();
        foreach (var observation in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(observation);
            if (observation.Parents.IsDefault)
                throw new ArgumentException(
                    "Folder parents must be initialized.",
                    nameof(observations)
                );
            if (!indices.Add(observation.Reference.RecordIndex))
                throw new InvalidDataException(
                    "Duplicate folder record identity in one generation."
                );
            var parents = observation.Parents.Distinct().ToArray();
            var issue = parents.Length switch
            {
                0 => FolderIssue.MissingParent,
                1 => FolderIssue.None,
                _ => FolderIssue.AmbiguousParent,
            };
            if (observation.Reference == root && (parents.Length != 1 || parents[0] != root))
                issue = FolderIssue.InvalidRoot;
            nodes.Add(
                observation.Reference,
                new(observation.Reference, parents.Length == 1 ? parents[0] : null, null, issue)
            );
        }
        var order = new List<FileReference>();
        if (!nodes.ContainsKey(root))
        {
            foreach (var key in nodes.Keys.ToArray())
                nodes[key] = nodes[key] with { Issue = FolderIssue.MissingRoot };
            return new(root, nodes, order);
        }
        if (nodes[root].Issue == FolderIssue.None)
        {
            nodes[root] = nodes[root] with { Depth = 0 };
            order.Add(root);
        }
        foreach (var start in nodes.Keys.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nodes[start].Issue != FolderIssue.None || nodes[start].Depth is not null)
                continue;
            var path = new List<FileReference>();
            var positions = new Dictionary<FileReference, int>();
            var current = start;
            while (nodes[current].Issue == FolderIssue.None && nodes[current].Depth is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (positions.TryGetValue(current, out int cycleStart))
                {
                    for (int i = cycleStart; i < path.Count; i++)
                        nodes[path[i]] = nodes[path[i]] with { Issue = FolderIssue.Cycle };
                    break;
                }
                positions.Add(current, path.Count);
                path.Add(current);
                var parent = nodes[current].Parent!.Value;
                if (!nodes.ContainsKey(parent))
                {
                    nodes[current] = nodes[current] with
                    {
                        Issue = indices.Contains(parent.RecordIndex)
                            ? FolderIssue.ReusedParent
                            : FolderIssue.MissingParent,
                    };
                    break;
                }
                current = parent;
            }
            for (int i = path.Count - 1; i >= 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = nodes[path[i]];
                if (node.Issue != FolderIssue.None)
                    continue;
                var parent = nodes[node.Parent!.Value];
                if (parent.Depth is not { } depth)
                    nodes[node.Reference] = node with { Issue = FolderIssue.InvalidAncestor };
                else
                {
                    nodes[node.Reference] = node with { Depth = checked(depth + 1) };
                    order.Add(node.Reference);
                }
            }
        }
        return new(root, nodes, order);
    }

    public FileReference? FindCommonAncestor(
        IEnumerable<FileReference> folders,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(folders);
        cancellationToken.ThrowIfCancellationRequested();
        FileReference? common = null;
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Nodes.TryGetValue(folder, out var node) || node.Depth is null)
                return null;
            if (common is null)
            {
                common = folder;
                continue;
            }
            var left = Nodes[common.Value];
            var right = node;
            while (left.Reference != right.Reference)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (left.Depth >= right.Depth)
                    left = Nodes[left.Parent!.Value];
                else
                    right = Nodes[right.Parent!.Value];
            }
            common = left.Reference;
        }
        return common;
    }
}
