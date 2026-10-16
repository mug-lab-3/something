using System.Collections.Immutable;
using Something.Ntfs;

namespace Something.Statistics;

/// <summary>A replaceable read-time projection. Never changes acquired or persisted facts.</summary>
public sealed class VisibleScan
{
    public FolderAggregationResult Aggregation { get; }
    public FolderGraph Graph { get; }
    public ImmutableHashSet<FileReference> ExcludedFolders { get; }

    public VisibleScan(
        FileReference root,
        Func<IEnumerable<FileCapacityObservation>> readFiles,
        FileVisibilityOptions options,
        CancellationToken token = default
    )
    {
        token.ThrowIfCancellationRequested();
        var directories = readFiles().Where(f => f.IsDirectory).ToArray();
        var graph = FolderGraph.Build(root, directories.Select(ToFolder), token);
        var excluded = directories
            .Where(f => f.Reference != root && !options.Includes(f.StandardFileAttributes))
            .Select(f => f.Reference)
            .ToHashSet();
        foreach (var reference in graph.ParentFirst)
        {
            token.ThrowIfCancellationRequested();
            if (reference != root && excluded.Contains(graph.Nodes[reference].Parent!.Value))
                excluded.Add(reference);
        }
        ExcludedFolders = excluded.ToImmutableHashSet();
        var visibleGraph = FolderGraph.Build(
            root,
            directories.Where(f => !excluded.Contains(f.Reference)).Select(ToFolder),
            token
        );
        Graph = visibleGraph;
        Aggregation = new FolderAggregator(visibleGraph).Aggregate(Filter(), token);

        IEnumerable<FileCapacityObservation> Filter()
        {
            foreach (var file in readFiles())
            {
                token.ThrowIfCancellationRequested();
                if (file.Reference == root)
                {
                    yield return file;
                    continue;
                }
                if (
                    !options.Includes(file.StandardFileAttributes)
                    || excluded.Contains(file.Reference)
                )
                    continue;
                var names = file.Names.Where(n => !excluded.Contains(n.Parent)).ToImmutableArray();
                // A deliberately excluded link is not an unresolved read. Preserve unnamed
                // or incomplete evidence when it was already missing in the original facts.
                if (
                    !file.IsDirectory
                    && file.Names.Any(n => n.Namespace != FileNameNamespace.Dos)
                    && !names.Any(n => n.Namespace != FileNameNamespace.Dos)
                )
                    continue;
                yield return file with
                {
                    Names = names,
                };
            }
        }
    }

    private static FolderObservation ToFolder(FileCapacityObservation file) =>
        new(
            file.Reference,
            [
                .. file
                    .Names.Where(n => n.Namespace != FileNameNamespace.Dos)
                    .Select(n => n.Parent)
                    .Distinct(),
            ]
        );
}
