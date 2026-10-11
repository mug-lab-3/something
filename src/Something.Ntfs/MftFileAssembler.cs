using System.Collections.Immutable;

namespace Something.Ntfs;

public sealed class NtfsAssemblyException(string message) : Exception(message);

/// <summary>Reported stream sizes, not the total space consumed by NTFS metadata.</summary>
public sealed record NtfsDataStream(
    string Name,
    bool IsResident,
    long LogicalBytes,
    long AllocatedBytes,
    ImmutableArray<DataRun> Runs
);

public sealed record NtfsFile(
    FileReference Reference,
    bool IsDirectory,
    ImmutableArray<NtfsFileName> Names,
    ImmutableArray<NtfsDataStream> DataStreams,
    ImmutableArray<NtfsAttribute> Attributes
);

/// <summary>
/// Resolves a complete file from record segments. Nonresident ATTRIBUTE_LIST bytes must be supplied
/// by acquisition; this class never follows disk addresses itself. Missing/stale references fail explicitly.
/// </summary>
public sealed class MftFileAssembler(int bytesPerCluster)
{
    private readonly int clusterSize = ValidateClusterSize(bytesPerCluster);

    private static int ValidateClusterSize(int size)
    {
        if (
            size < 512
            || size > 2 * 1024 * 1024
            || !System.Numerics.BitOperations.IsPow2((uint)size)
        )
            throw new ArgumentOutOfRangeException(nameof(bytesPerCluster));
        return size;
    }

    public NtfsFile Assemble(
        MftRecord baseRecord,
        IEnumerable<MftRecord> extensions,
        ReadOnlyMemory<byte>? nonResidentAttributeListValue = null
    )
    {
        ArgumentNullException.ThrowIfNull(baseRecord);
        ArgumentNullException.ThrowIfNull(extensions);
        if (!baseRecord.IsInUse || baseRecord.IsExtension)
            throw new NtfsAssemblyException("Expected an active base record.");
        var records = new Dictionary<FileReference, MftRecord>
        {
            [baseRecord.Reference] = baseRecord,
        };
        foreach (var extension in extensions)
        {
            if (!extension.IsInUse || extension.BaseReference != baseRecord.Reference)
                throw new NtfsAssemblyException(
                    "Extension has a stale or different base file reference."
                );
            if (
                extension.Reference.RecordIndex == baseRecord.Reference.RecordIndex
                || records.Keys.Any(reference =>
                    reference.RecordIndex == extension.Reference.RecordIndex
                )
                || !records.TryAdd(extension.Reference, extension)
            )
                throw new NtfsAssemblyException("Duplicate record index.");
        }

        var listAttributes = baseRecord
            .Attributes.Where(a => a.Type == NtfsAttributeTypes.AttributeList)
            .ToArray();
        if (listAttributes.Length > 1)
            throw new NtfsAssemblyException("Multiple ATTRIBUTE_LIST attributes.");
        var attributes = ImmutableArray.CreateBuilder<NtfsAttribute>();
        if (listAttributes.Length == 0)
        {
            if (records.Count != 1 || nonResidentAttributeListValue is not null)
                throw new NtfsAssemblyException(
                    "Extensions/list bytes supplied without ATTRIBUTE_LIST."
                );
            attributes.AddRange(baseRecord.Attributes);
        }
        else
        {
            var list = listAttributes[0];
            if (list.Name.Length != 0)
                throw new NtfsAssemblyException("ATTRIBUTE_LIST must be unnamed.");
            ReadOnlyMemory<byte> listBytes;
            if (list is ResidentAttribute resident)
            {
                if (nonResidentAttributeListValue is not null)
                    throw new NtfsAssemblyException(
                        "External bytes supplied for resident ATTRIBUTE_LIST."
                    );
                listBytes = resident.Value.AsMemory();
            }
            else if (
                list is NonResidentAttribute nonResident
                && nonResident.LowestVcn == 0
                && nonResident.Sizes is { } sizes
                && nonResidentAttributeListValue is { } supplied
                && sizes.LogicalBytes == supplied.Length
                && sizes.InitializedBytes == sizes.LogicalBytes
            )
                listBytes = supplied;
            else
                throw new NtfsAssemblyException(
                    "Complete nonresident ATTRIBUTE_LIST value is required."
                );

            var entries = AttributeValueParser.ParseAttributeList(listBytes.Span);
            var visited = new HashSet<(FileReference Record, ushort Id)>();
            foreach (var entry in entries)
            {
                if (entry.Type == NtfsAttributeTypes.AttributeList)
                    throw new NtfsAssemblyException("ATTRIBUTE_LIST cannot reference itself.");
                if (!records.TryGetValue(entry.Record, out var record))
                    throw new NtfsAssemblyException(
                        "Missing record or sequence number mismatch in ATTRIBUTE_LIST."
                    );
                var matches = record
                    .Attributes.Where(a =>
                        a.Type == entry.Type
                        && a.Name == entry.Name
                        && (a is NonResidentAttribute n ? n.LowestVcn : 0) == entry.LowestVcn
                        &&
                        // Continuation list entries may carry zero instead of the instance number.
                        (
                            entry.LowestVcn != 0 && entry.AttributeId == 0
                            || a.Id == entry.AttributeId
                        )
                    )
                    .ToArray();
                if (matches.Length != 1)
                    throw new NtfsAssemblyException("Missing or ambiguous listed attribute.");
                var match = matches[0];
                if (!visited.Add((entry.Record, match.Id)))
                    throw new NtfsAssemblyException("Duplicate ATTRIBUTE_LIST entry.");
                attributes.Add(match);
            }
            foreach (var record in records.Values)
            foreach (var attribute in record.Attributes)
                if (
                    attribute.Type != NtfsAttributeTypes.AttributeList
                    && !visited.Contains((record.Reference, attribute.Id))
                )
                    throw new NtfsAssemblyException("Record contains an unlisted attribute.");
            attributes.Add(list);
        }

        var names = attributes
            .OfType<ResidentAttribute>()
            .Where(a => a.Type == NtfsAttributeTypes.FileName)
            .Select(a => AttributeValueParser.ParseFileName(a.Value.AsSpan()))
            .ToImmutableArray();
        var streams = ImmutableArray.CreateBuilder<NtfsDataStream>();
        foreach (
            var group in attributes
                .Where(a => a.Type == NtfsAttributeTypes.Data)
                .GroupBy(a => a.Name, StringComparer.Ordinal)
        )
            streams.Add(AssembleStream(group.Key, group.ToArray()));
        return new(
            baseRecord.Reference,
            baseRecord.IsDirectory,
            names,
            streams.ToImmutable(),
            attributes.ToImmutable()
        );
    }

    private NtfsDataStream AssembleStream(string name, NtfsAttribute[] attributes)
    {
        if (attributes is [ResidentAttribute resident])
            return new(name, true, resident.Value.Length, 0, []);
        if (attributes.Any(a => a is not NonResidentAttribute))
            throw new NtfsAssemblyException("Mixed or duplicate resident DATA attributes.");
        var extents = attributes.Cast<NonResidentAttribute>().OrderBy(a => a.LowestVcn).ToArray();
        if (extents[0].LowestVcn != 0 || extents[0].Sizes is not { } sizes)
            throw new NtfsAssemblyException("Missing first DATA extent.");
        long nextVcn = 0;
        var runs = ImmutableArray.CreateBuilder<DataRun>();
        foreach (var extent in extents)
        {
            if (
                extent.LowestVcn != nextVcn
                || extent.Flags != extents[0].Flags
                || extent.CompressionUnit != extents[0].CompressionUnit
            )
                throw new NtfsAssemblyException(
                    "DATA extents overlap, have gaps, or have incompatible flags."
                );
            nextVcn = checked(extent.HighestVcn + 1);
            runs.AddRange(extent.Runs);
        }
        if (nextVcn == 0 && sizes.LogicalBytes != 0)
            throw new NtfsAssemblyException("Nonempty stream has no mapping pairs.");
        try
        {
            long mappedBytes = checked(nextVcn * clusterSize);
            long physicalBytes = 0;
            foreach (var run in runs)
                if (run.StartLcn is not null)
                    physicalBytes = checked(
                        physicalBytes + checked(run.ClusterCount * clusterSize)
                    );
            if (
                sizes.LogicalBytes > mappedBytes
                || physicalBytes != (sizes.PhysicalAllocatedBytes ?? sizes.AllocatedBytes)
            )
                throw new NtfsAssemblyException(
                    "DATA sizes disagree with complete mapping pairs and volume geometry."
                );
        }
        catch (OverflowException)
        {
            throw new NtfsAssemblyException("DATA byte count overflow.");
        }
        return new(
            name,
            false,
            sizes.LogicalBytes,
            sizes.PhysicalAllocatedBytes ?? sizes.AllocatedBytes,
            runs.ToImmutable()
        );
    }
}
