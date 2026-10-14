using Something.Ntfs;
using Something.Statistics;

namespace Something.Storage;

// Version IDs identify immutable data, never the lifetime of their originating scan.
internal sealed class SharedFolderReferenceEntity
{
    public Guid ScanId { get; set; }
    public long RecordIndex { get; set; }
    public ushort Sequence { get; set; }
    public Guid? ObservationVersion { get; set; }
    public Guid SampleVersion { get; set; }
}

internal sealed class SharedAuxiliaryReferenceEntity
{
    public Guid ScanId { get; set; }
    public long RecordIndex { get; set; }
    public Guid VersionId { get; set; }
}

internal sealed class SharedListingReferenceEntity
{
    public Guid ScanId { get; set; }
    public long ParentIndex { get; set; }
    public ushort ParentSequence { get; set; }
    public long Bucket { get; set; }
    public Guid ListingId { get; set; }
}

internal sealed class SharedEntryEntity
{
    public Guid ScanId { get; set; }
    public long RecordIndex { get; set; }
    public int Ordinal { get; set; }
    public Guid ObservationVersion { get; set; }
    public string Name { get; set; } = "";
    public ushort Sequence { get; set; }
    public bool IsDirectory { get; set; }
    public CapacityResolution Resolution { get; set; }
    public bool LinksComplete { get; set; }
    public long? StreamLogical { get; set; }
    public long? StreamAllocated { get; set; }
}

internal sealed class StateObservationEntity
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
}

internal sealed class StateFileNameEntity
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

internal sealed class StateStreamEntity
{
    public Guid ScanId { get; set; }
    public long RecordIndex { get; set; }
    public int Ordinal { get; set; }
    public string Name { get; set; } = "";
    public long Logical { get; set; }
    public long Allocated { get; set; }
}

internal sealed class StateFolderSampleEntity
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
