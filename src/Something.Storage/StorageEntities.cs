using Something.Ntfs;
using Something.Statistics;

namespace Something.Storage;

internal sealed class ScanEntity
{
    public Guid Id { get; set; }
    public string VolumeKey { get; set; } = "";
    public Guid Epoch { get; set; }
    public ulong Serial { get; set; }
    public long RangeStart { get; set; }
    public long RangeCount { get; set; }
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset? Ended { get; set; }
    public StoredScanState State { get; set; }
    public string ObservationMode { get; set; } = "OptimisticLive";
    public string AttributionPolicy { get; set; } = "CommonAncestor-v1";
    public string CapacityPolicy { get; set; } = "AllData-v1";
}

internal sealed class ObservationEntity
{
    public Guid ScanId { get; set; }
    public long RecordIndex { get; set; }
    public MftScanEntryKind Kind { get; set; }
    public LiveFileReadStatus Status { get; set; }
    public ushort? Sequence { get; set; }
    public bool? IsDirectory { get; set; }
    public CapacityDomain? Domain { get; set; }
    public CapacityResolution? Resolution { get; set; }
    public bool? LinksComplete { get; set; }
    public ushort? ObservedLinkCount { get; set; }
    public List<FileNameEntity> Names { get; set; } = [];
    public List<StreamEntity> Streams { get; set; } = [];
}

internal sealed class FileNameEntity
{
    public Guid ScanId { get; set; }
    public long RecordIndex { get; set; }
    public int Ordinal { get; set; }
    public long ParentIndex { get; set; }
    public ushort ParentSequence { get; set; }
    public string Name { get; set; } = "";
    public FileNameNamespace Namespace { get; set; }
    public uint Attributes { get; set; }
}

internal sealed class StreamEntity
{
    public Guid ScanId { get; set; }
    public long RecordIndex { get; set; }
    public int Ordinal { get; set; }
    public string Name { get; set; } = "";
    public long Logical { get; set; }
    public long Allocated { get; set; }
}

internal sealed class FolderSampleEntity
{
    public Guid ScanId { get; set; }
    public long RecordIndex { get; set; }
    public ushort Sequence { get; set; }
    public UInt128 DirectLogical { get; set; }
    public UInt128 DirectAllocated { get; set; }
    public UInt128 SharedLogical { get; set; }
    public UInt128 SharedAllocated { get; set; }
    public UInt128 DirectoryLogical { get; set; }
    public UInt128 DirectoryAllocated { get; set; }
    public UInt128 SubtreeLogical { get; set; }
    public UInt128 SubtreeAllocated { get; set; }
    public long AssignedCount { get; set; }
    public long SubtreeCount { get; set; }
    public long DirectUnresolved { get; set; }
    public long SubtreeUnresolved { get; set; }
    public bool DirectoryObserved { get; set; }
    public long UnobservedDirectories { get; set; }
    public UInt128? EntryDirectLogical { get; set; }
    public UInt128? EntryDirectAllocated { get; set; }
    public UInt128? EntrySubtreeLogical { get; set; }
    public UInt128? EntrySubtreeAllocated { get; set; }
    public long? EntryDirectFiles { get; set; }
    public long? EntrySubtreeFiles { get; set; }
    public long? EntryDirectUnresolved { get; set; }
    public long? EntrySubtreeUnresolved { get; set; }
}

internal sealed class FolderNameEntity
{
    public Guid ScanId { get; set; }
    public long RecordIndex { get; set; }
    public int Ordinal { get; set; }
    public long ParentIndex { get; set; }
    public ushort ParentSequence { get; set; }
    public string Name { get; set; } = "";
    public FileNameNamespace Namespace { get; set; }
    public uint Attributes { get; set; }
}

internal sealed class VolumeSampleEntity
{
    public Guid ScanId { get; set; }
    public UInt128 MetadataLogical { get; set; }
    public UInt128 MetadataAllocated { get; set; }
    public long MetadataResolved { get; set; }
    public long MetadataUnresolved { get; set; }
    public UInt128 UnattributedLogical { get; set; }
    public UInt128 UnattributedAllocated { get; set; }
    public long UnattributedResolved { get; set; }
    public long UnattributedUnresolved { get; set; }
    public bool UnknownAttribution { get; set; }
}

internal sealed class PublishedGenerationEntity
{
    public string VolumeKey { get; set; } = "";
    public long RangeStart { get; set; }
    public long RangeCount { get; set; }
    public Guid ScanId { get; set; }
}
