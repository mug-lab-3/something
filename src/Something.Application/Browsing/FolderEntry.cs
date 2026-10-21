using System.Collections.Immutable;
using Something.Ntfs;
using Something.Statistics;

namespace Something.Application.Browsing;

public enum FolderEntrySort
{
    AllocatedBytes,
    LogicalBytes,
}

public sealed record FolderEntry(
    FileReference Reference,
    int NameOrdinal,
    string Name,
    bool IsDirectory,
    CapacityAmount? Data,
    bool NamesComplete,
    bool CanBrowse,
    bool CapacityIncomplete
)
{
    public uint? StandardFileAttributes { get; init; }
    public bool? IsHidden =>
        StandardFileAttributes is { } attributes ? (attributes & 0x2) != 0 : null;
    public bool? IsSystem =>
        StandardFileAttributes is { } attributes ? (attributes & 0x4) != 0 : null;
}

public sealed record FolderEntryPage(
    ImmutableArray<FolderEntry> Entries,
    bool HasMore,
    long? TotalCount = null
);
