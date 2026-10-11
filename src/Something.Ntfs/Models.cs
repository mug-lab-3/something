using System.Collections.Immutable;

namespace Something.Ntfs;

/// <summary>Record index plus reuse sequence. A path or record index alone is not a file identity.</summary>
public readonly record struct FileReference(ulong Value)
{
    public const ulong MaximumRecordIndex = 0x0000_FFFF_FFFF_FFFF;
    public ulong RecordIndex => Value & MaximumRecordIndex;
    public ushort SequenceNumber => (ushort)(Value >> 48);

    public static FileReference Create(ulong recordIndex, ushort sequenceNumber)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(recordIndex, MaximumRecordIndex);
        return new(recordIndex | ((ulong)sequenceNumber << 48));
    }
}

[Flags]
public enum MftRecordFlags : ushort
{
    InUse = 1,
    Directory = 2,
}

[Flags]
public enum NtfsAttributeFlags : ushort
{
    Compressed = 1,
    Encrypted = 0x4000,
    Sparse = 0x8000,
}

public enum FileNameNamespace : byte
{
    Posix,
    Win32,
    Dos,
    Win32AndDos,
}

public static class NtfsAttributeTypes
{
    public const uint StandardInformation = 0x10;
    public const uint AttributeList = 0x20;
    public const uint FileName = 0x30;
    public const uint Data = 0x80;
    public const uint ReparsePoint = 0xC0;
}

/// <summary>LCN is null for a sparse hole. VCN and length are in clusters, not bytes.</summary>
public readonly record struct DataRun(long StartVcn, long ClusterCount, long? StartLcn);

/// <summary>Only valid on the extent whose lowest VCN is zero.</summary>
public sealed record StreamSizes(
    long AllocatedBytes,
    long LogicalBytes,
    long InitializedBytes,
    long? PhysicalAllocatedBytes
);

public abstract record NtfsAttribute(uint Type, ushort Id, string Name, NtfsAttributeFlags Flags);

public sealed record ResidentAttribute(
    uint Type,
    ushort Id,
    string Name,
    NtfsAttributeFlags Flags,
    ImmutableArray<byte> Value
) : NtfsAttribute(Type, Id, Name, Flags);

public sealed record NonResidentAttribute(
    uint Type,
    ushort Id,
    string Name,
    NtfsAttributeFlags Flags,
    long LowestVcn,
    long HighestVcn,
    ushort CompressionUnit,
    StreamSizes? Sizes,
    ImmutableArray<DataRun> Runs
) : NtfsAttribute(Type, Id, Name, Flags);

/// <summary>Preserves every name, including DOS aliases and distinct hard links.</summary>
public sealed record NtfsFileName(
    FileReference Parent,
    string Name,
    FileNameNamespace Namespace,
    uint FileAttributes
);

public sealed record AttributeListEntry(
    uint Type,
    ushort AttributeId,
    string Name,
    long LowestVcn,
    FileReference Record
);

/// <summary>A record segment, not necessarily a complete file. Extensions have a nonzero base reference.</summary>
public sealed record MftRecord(
    FileReference Reference,
    FileReference BaseReference,
    MftRecordFlags Flags,
    ushort HardLinkCount,
    ulong LogSequenceNumber,
    ImmutableArray<NtfsAttribute> Attributes
)
{
    public bool IsInUse => (Flags & MftRecordFlags.InUse) != 0;
    public bool IsDirectory => (Flags & MftRecordFlags.Directory) != 0;
    public bool IsExtension => BaseReference.Value != 0;
}
