using System.Collections.Immutable;
using Something.Ntfs;

namespace Something.Statistics;

/// <summary>Converts complete live files into owned compact facts. Unknown identity remains an issue, never a fabricated reference.</summary>
public static class MftObservationAdapter
{
    public static FileCapacityObservation? Convert(
        MftScanEntry entry,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        if (
            !Enum.IsDefined(entry.Kind)
            || !Enum.IsDefined(entry.Status)
            || entry.RecordIndex > FileReference.MaximumRecordIndex
        )
            throw new ArgumentException("Invalid scan entry.", nameof(entry));
        if (entry.Kind == MftScanEntryKind.ExtensionRecord)
        {
            if (
                entry.File is not null
                || entry.Evidence is not null
                || entry.Status != LiveFileReadStatus.ObservedUnchanged
            )
                throw new InvalidDataException(
                    "An extension cannot carry a separate file capacity."
                );
            return null;
        }
        if (entry.Kind == MftScanEntryKind.ResolvedFile)
        {
            if (
                entry.Status != LiveFileReadStatus.ObservedUnchanged
                || entry.File is not { } file
                || entry.Evidence is not { } evidence
                || evidence.Reference != file.Reference
                || evidence.IsDirectory != file.IsDirectory
                || file.Reference.RecordIndex != entry.RecordIndex
            )
                throw new InvalidDataException("Complete file and acquisition evidence disagree.");
            if (file.Names.IsDefault || file.DataStreams.IsDefault)
                throw new InvalidDataException("Acquired arrays must be initialized.");
            var names = new HashSet<(FileReference Parent, string Name)>();
            foreach (var name in file.Names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    name is null
                    || !Enum.IsDefined(name.Namespace)
                    || string.IsNullOrEmpty(name.Name)
                )
                    throw new InvalidDataException("Invalid acquired file name.");
                if (name.Namespace != FileNameNamespace.Dos)
                    names.Add((name.Parent, name.Name));
            }
            bool complete =
                file.IsDirectory || (names.Count > 0 && names.Count == evidence.HardLinkCount);
            var streams = ImmutableArray.CreateBuilder<StreamCapacity>();
            foreach (var stream in file.DataStreams)
            {
                cancellationToken.ThrowIfCancellationRequested();
                streams.Add(new(stream.Name, stream.LogicalBytes, stream.AllocatedBytes));
            }
            return new(
                file.Reference,
                file.IsDirectory,
                Domain(evidence),
                CapacityResolution.Resolved,
                file.Names,
                streams.ToImmutable(),
                complete
            )
            {
                ObservedHardLinkCount = evidence.HardLinkCount,
            };
        }
        if (entry.File is not null || entry.Status == LiveFileReadStatus.ObservedUnchanged)
            throw new InvalidDataException(
                "Unresolved scan entries cannot carry complete capacities."
            );
        if (entry.Evidence is not { } observed)
            return null;
        if (observed.Reference.RecordIndex != entry.RecordIndex)
            throw new InvalidDataException("Unresolved identity differs from the scan slot.");
        return new(
            observed.Reference,
            observed.IsDirectory,
            Domain(observed),
            CapacityResolution.Unresolved,
            [],
            [],
            false
        )
        {
            ObservedHardLinkCount = observed.HardLinkCount,
        };
    }

    private static CapacityDomain Domain(MftFileEvidence evidence)
    {
        if (!Enum.IsDefined(evidence.MetadataKind))
            throw new InvalidDataException("Unknown metadata classification.");
        return
            evidence.MetadataKind == NtfsMetadataKind.BadClusters
            || (evidence.Reference.RecordIndex < 16 && evidence.Reference.RecordIndex != 5)
            ? CapacityDomain.NtfsMetadata
            : CapacityDomain.Ordinary;
    }
}

/// <summary>Resolves reserved-directory ancestry after the complete folder graph is available.</summary>
public sealed class ScanDomainResolver(FolderGraph graph)
{
    private readonly Dictionary<FileReference, CapacityDomain> domains = BuildDomains(graph);

    private static Dictionary<FileReference, CapacityDomain> BuildDomains(FolderGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var domains = new Dictionary<FileReference, CapacityDomain>();
        foreach (var reference in graph.ParentFirst)
            domains[reference] =
                reference == graph.Root ? CapacityDomain.Ordinary
                : reference.RecordIndex < 16
                || domains[graph.Nodes[reference].Parent!.Value] == CapacityDomain.NtfsMetadata
                    ? CapacityDomain.NtfsMetadata
                : CapacityDomain.Ordinary;
        return domains;
    }

    public FileCapacityObservation Resolve(
        FileCapacityObservation observation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        if (observation.Domain != CapacityDomain.Ordinary)
            return observation;
        if (observation.IsDirectory)
            return observation with
            {
                Domain = domains.GetValueOrDefault(
                    observation.Reference,
                    CapacityDomain.Unclassified
                ),
            };
        if (!observation.LinkSetComplete || observation.Names.IsEmpty)
            return observation with { Domain = CapacityDomain.Unclassified };
        CapacityDomain? domain = null;
        foreach (var name in observation.Names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (name.Namespace == FileNameNamespace.Dos)
                continue;
            var parentDomain = domains.GetValueOrDefault(name.Parent, CapacityDomain.Unclassified);
            if (
                parentDomain == CapacityDomain.Unclassified
                || (domain is { } previous && previous != parentDomain)
            )
                return observation with { Domain = CapacityDomain.Unclassified };
            domain = parentDomain;
        }
        return observation with { Domain = domain ?? CapacityDomain.Unclassified };
    }
}
