using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Something.Ntfs;
using Something.Statistics;

namespace Something.Storage;

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
);

public sealed record FolderEntryPage(ImmutableArray<FolderEntry> Entries, bool HasMore);

public sealed partial class ScanStore
{
    /// <summary>Immediate visible entries only. Preserves aliases and sequence identities;
    /// unresolved sizes stay null. Folder sizes include descendants; files show unnamed DATA.</summary>
    public FolderEntryPage ReadFolderEntries(
        Guid scanId,
        FileReference parent,
        int page = 0,
        int pageSize = 250,
        bool directoriesOnly = false,
        CancellationToken cancellationToken = default,
        FolderEntrySort sort = FolderEntrySort.AllocatedBytes
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(sort))
            throw new ArgumentOutOfRangeException(nameof(sort));
        if (page < 0 || pageSize is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(page));
        int offset = checked(page * pageSize);
        RequireCompleted(scanId);
        long parentIndex = checked((long)parent.RecordIndex);
        if (
            !context.Folders.Any(f =>
                f.ScanId == scanId
                && f.RecordIndex == parentIndex
                && f.Sequence == parent.SequenceNumber
            )
        )
            throw new InvalidOperationException(
                "Folder identity is not present in this completed generation."
            );
        var query =
            from name in context.Names.AsNoTracking()
            join observation in context.Observations.AsNoTracking()
                on new { name.ScanId, name.RecordIndex } equals new
                {
                    observation.ScanId,
                    observation.RecordIndex,
                }
            join folder in context.Folders.AsNoTracking()
                on new { name.ScanId, name.RecordIndex } equals new
                {
                    folder.ScanId,
                    folder.RecordIndex,
                }
                into folderGroup
            from folder in folderGroup.DefaultIfEmpty()
            join stream in context.Streams.AsNoTracking().Where(s => s.Name == "")
                on new { name.ScanId, name.RecordIndex } equals new
                {
                    stream.ScanId,
                    stream.RecordIndex,
                }
                into streamGroup
            from stream in streamGroup.DefaultIfEmpty()
            where
                name.ScanId == scanId
                && name.ParentIndex == parentIndex
                && name.ParentSequence == parent.SequenceNumber
                && name.Namespace != FileNameNamespace.Dos
                && observation.Sequence != null
                && observation.IsDirectory != null
                && observation.Domain == CapacityDomain.Ordinary
                && observation.RecordIndex >= 16
                && (!directoriesOnly || observation.IsDirectory == true && folder != null)
            select new
            {
                name.RecordIndex,
                name.Ordinal,
                name.Name,
                observation.Sequence,
                observation.IsDirectory,
                observation.Resolution,
                observation.LinksComplete,
                FolderLogical = (UInt128?)folder.EntrySubtreeLogical,
                FolderAllocated = (UInt128?)folder.EntrySubtreeAllocated,
                FolderSequence = (ushort?)folder.Sequence,
                Unresolved = (long?)folder.EntrySubtreeUnresolved,
                Unobserved = (long?)folder.UnobservedDirectories,
                StreamLogical = observation.IsDirectory == true
                || observation.Resolution != CapacityResolution.Resolved
                    ? (long?)null
                    : ((long?)stream.Logical ?? 0),
                StreamAllocated = observation.IsDirectory == true
                || observation.Resolution != CapacityResolution.Resolved
                    ? (long?)null
                    : ((long?)stream.Allocated ?? 0),
            };
        var ordered =
            sort == FolderEntrySort.AllocatedBytes
                ? query
                    .OrderByDescending(r => r.IsDirectory)
                    .ThenByDescending(r => r.FolderAllocated)
                    .ThenByDescending(r => r.StreamAllocated)
                : query
                    .OrderByDescending(r => r.IsDirectory)
                    .ThenByDescending(r => r.FolderLogical)
                    .ThenByDescending(r => r.StreamLogical);
        var rows = ordered
            .ThenBy(r => r.RecordIndex)
            .ThenBy(r => r.Ordinal)
            .Skip(offset)
            .Take(pageSize + 1)
            .ToArray();
        var entries = ImmutableArray.CreateBuilder<FolderEntry>();
        foreach (var row in rows.Take(pageSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool directory = row.IsDirectory!.Value;
            bool canBrowse = directory && row.FolderSequence == row.Sequence;
            CapacityAmount? data = directory
                ? canBrowse
                && row.FolderLogical is { } logical
                && row.FolderAllocated is { } allocated
                    ? new(logical, allocated)
                    : null
                : row.Resolution == CapacityResolution.Resolved
                    ? new(
                        checked((UInt128)(row.StreamLogical ?? 0)),
                        checked((UInt128)(row.StreamAllocated ?? 0))
                    )
                    : null;
            entries.Add(
                new(
                    FileReference.Create(checked((ulong)row.RecordIndex), row.Sequence!.Value),
                    row.Ordinal,
                    row.Name,
                    directory,
                    data,
                    row.LinksComplete ?? false,
                    canBrowse,
                    data is null
                        || directory && ((row.Unresolved ?? 0) != 0 || (row.Unobserved ?? 0) != 0)
                )
            );
        }
        return new(entries.ToImmutable(), rows.Length > pageSize);
    }
}
