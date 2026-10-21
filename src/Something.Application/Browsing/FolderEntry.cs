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
        StandardFileAttributes is { } attributes
            ? ((FileAttributes)attributes).HasFlag(FileAttributes.Hidden)
            : null;
    public bool? IsSystem =>
        StandardFileAttributes is { } attributes
            ? ((FileAttributes)attributes).HasFlag(FileAttributes.System)
            : null;
}

public sealed record FolderEntryPage(
    ImmutableArray<FolderEntry> Entries,
    bool HasMore,
    long? TotalCount = null
);
