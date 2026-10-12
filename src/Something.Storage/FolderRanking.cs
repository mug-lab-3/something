using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Something.Ntfs;
using Something.Statistics;

namespace Something.Storage;

/// <summary>Paths are observed names for display, not paths to open or follow.</summary>
public sealed record RankedFolder(string RelativePath, FolderCapacitySample Sample);

public sealed partial class ScanStore
{
    /// <summary>Ranks optimistic subtree DATA; excludes root and NTFS metadata. Nested rows are not additive.</summary>
    public ImmutableArray<RankedFolder> ReadLargestFolders(
        Guid scanId,
        int count = 5,
        CancellationToken cancellationToken = default
    )
    {
        if (count is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(count));
        cancellationToken.ThrowIfCancellationRequested();
        var aggregation = ReadAggregation(scanId, cancellationToken);
        var names = new Dictionary<ulong, List<FolderNameEntity>>();
        foreach (
            var name in context
                .FolderNames.AsNoTracking()
                .Where(n => n.ScanId == scanId)
                .AsEnumerable()
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong index = checked((ulong)name.RecordIndex);
            if (!names.TryGetValue(index, out var list))
                names.Add(index, list = []);
            list.Add(name);
        }
        var root = aggregation.Folders.Keys.Single(r => r.RecordIndex == 5);
        var graph = FolderGraph.Build(
            root,
            aggregation.Folders.Keys.Select(r => new FolderObservation(
                r,
                [
                    .. names
                        .GetValueOrDefault(r.RecordIndex, [])
                        .Where(n => n.Namespace != FileNameNamespace.Dos)
                        .Select(n =>
                            FileReference.Create(checked((ulong)n.ParentIndex), n.ParentSequence)
                        )
                        .Distinct(),
                ]
            )),
            cancellationToken
        );
        var resolver = new ScanDomainResolver(graph);
        bool Ordinary(FolderCapacitySample sample) =>
            resolver
                .Resolve(
                    new(
                        sample.Reference,
                        true,
                        CapacityDomain.Ordinary,
                        CapacityResolution.Resolved,
                        [],
                        [],
                        true
                    ),
                    cancellationToken
                )
                .Domain == CapacityDomain.Ordinary;
        var largest = aggregation
            .Folders.Values.Where(s => s.Reference != root && Ordinary(s))
            .OrderByDescending(s => s.SubtreeData.AllocatedBytes)
            .ThenBy(s => s.Reference.RecordIndex)
            .Take(count)
            .ToArray();
        var result = ImmutableArray.CreateBuilder<RankedFolder>();
        foreach (var sample in largest)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var components = new Stack<string>();
            var current = sample.Reference;
            while (current != root)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = graph.Nodes[current].Parent!.Value;
                var name = names[current.RecordIndex]
                    .Where(n =>
                        n.Namespace != FileNameNamespace.Dos
                        && (ulong)n.ParentIndex == parent.RecordIndex
                        && n.ParentSequence == parent.SequenceNumber
                    )
                    .OrderBy(n =>
                        n.Namespace == FileNameNamespace.Win32
                        || n.Namespace == FileNameNamespace.Win32AndDos
                            ? 0
                            : 1
                    )
                    .ThenBy(n => n.Name, StringComparer.Ordinal)
                    .First();
                components.Push(name.Name);
                current = parent;
            }
            result.Add(new(string.Join('\\', components), sample));
        }
        return result.ToImmutable();
    }
}
