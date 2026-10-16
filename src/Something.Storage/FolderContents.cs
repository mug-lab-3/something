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

public sealed partial class ScanStore
{
    /// <summary>Returns the unique observed parent for this visible folder name.
    /// Validates both sequence identities; ambiguous or missing parents remain unavailable.</summary>
    public FileReference? ReadParentFolder(
        Guid scanId,
        FileReference folder,
        string visibleName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(visibleName);
        cancellationToken.ThrowIfCancellationRequested();
        RequireCompleted(scanId);
        long index = checked((long)folder.RecordIndex);
        if (
            !context.Observations.Any(f =>
                f.ScanId == scanId
                && f.RecordIndex == index
                && f.Sequence == folder.SequenceNumber
                && f.IsDirectory == true
            )
        )
            throw new InvalidOperationException(
                "Folder identity is not present in this completed generation."
            );
        if (folder.RecordIndex == 5)
            return null;
        var parents = (
            from name in context.FolderNames.AsNoTracking()
            join parent in context.Folders.AsNoTracking()
                on new
                {
                    name.ScanId,
                    RecordIndex = name.ParentIndex,
                    Sequence = name.ParentSequence,
                } equals new
                {
                    parent.ScanId,
                    parent.RecordIndex,
                    parent.Sequence,
                }
            where
                name.ScanId == scanId
                && name.RecordIndex == index
                && name.Name == visibleName
                && name.Namespace != FileNameNamespace.Dos
                && parent.RecordIndex != index
            select new { parent.RecordIndex, parent.Sequence }
        )
            .Distinct()
            .Take(2)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return parents.Length == 1
            ? FileReference.Create(checked((ulong)parents[0].RecordIndex), parents[0].Sequence)
            : null;
    }

    /// <summary>Immediate visible entries only. Preserves aliases and sequence identities;
    /// unresolved sizes stay null. Folder sizes include descendants; files show unnamed DATA.</summary>
    public FolderEntryPage ReadFolderEntries(
        Guid scanId,
        FileReference parent,
        int page = 0,
        int pageSize = 250,
        bool directoriesOnly = false,
        CancellationToken cancellationToken = default,
        FolderEntrySort sort = FolderEntrySort.AllocatedBytes,
        bool includeTotalCount = false,
        bool descending = true
    )
    {
        if (page < 0 || pageSize is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(page));
        return ReadFolderEntriesCore(
            scanId,
            parent,
            page,
            pageSize,
            directoriesOnly,
            cancellationToken,
            sort,
            includeTotalCount,
            descending
        );
    }

    /// <summary>Reads one folder once before in-memory filtering, sorting and pagination.
    /// Does not repeat the same DB query for every 250 visible entries.</summary>
    public ImmutableArray<FolderEntry> ReadAllFolderEntries(
        Guid scanId,
        FileReference parent,
        CancellationToken cancellationToken = default
    ) =>
        ReadFolderEntriesCore(
            scanId,
            parent,
            0,
            null,
            false,
            cancellationToken,
            FolderEntrySort.AllocatedBytes,
            false,
            true
        ).Entries;

    private FolderEntryPage ReadFolderEntriesCore(
        Guid scanId,
        FileReference parent,
        int page,
        int? pageSize,
        bool directoriesOnly,
        CancellationToken cancellationToken,
        FolderEntrySort sort,
        bool includeTotalCount,
        bool descending
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(sort))
            throw new ArgumentOutOfRangeException(nameof(sort));
        int offset = checked(page * (pageSize ?? 0));
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
                observation.StandardFileAttributes,
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
        if (context is DuckScanDbContext)
            query = (
                context is SharedHistoryDbContext
                    ? SharedFolderRows(scanId, parent)
                    : context.Browser.AsNoTracking()
            )
                .Where(row =>
                    row.ScanId == scanId
                    && row.ParentIndex == parentIndex
                    && row.ParentSequence == parent.SequenceNumber
                    && (!directoriesOnly || row.IsDirectory == true && row.FolderSequence != null)
                )
                .Select(row => new
                {
                    row.RecordIndex,
                    row.Ordinal,
                    row.Name,
                    row.Sequence,
                    row.IsDirectory,
                    row.Resolution,
                    row.LinksComplete,
                    row.StandardFileAttributes,
                    FolderLogical = row.EntrySubtreeLogical,
                    FolderAllocated = row.EntrySubtreeAllocated,
                    FolderSequence = row.FolderSequence,
                    Unresolved = row.EntrySubtreeUnresolved,
                    Unobserved = row.UnobservedDirectories,
                    StreamLogical = row.IsDirectory == true
                    || row.Resolution != CapacityResolution.Resolved
                        ? (long?)null
                        : (row.StreamLogical ?? 0),
                    StreamAllocated = row.IsDirectory == true
                    || row.Resolution != CapacityResolution.Resolved
                        ? (long?)null
                        : (row.StreamAllocated ?? 0),
                });
        long? totalCount = includeTotalCount ? query.LongCount() : null;
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = !descending
            ? sort == FolderEntrySort.AllocatedBytes
                ? query
                    .OrderByDescending(r => r.IsDirectory)
                    .ThenBy(r => r.FolderAllocated == null && r.StreamAllocated == null)
                    .ThenBy(r => r.FolderAllocated)
                    .ThenBy(r => r.StreamAllocated)
                : query
                    .OrderByDescending(r => r.IsDirectory)
                    .ThenBy(r => r.FolderLogical == null && r.StreamLogical == null)
                    .ThenBy(r => r.FolderLogical)
                    .ThenBy(r => r.StreamLogical)
            : sort == FolderEntrySort.AllocatedBytes
                ? query
                    .OrderByDescending(r => r.IsDirectory)
                    .ThenByDescending(r => r.FolderAllocated)
                    .ThenByDescending(r => r.StreamAllocated)
                : query
                    .OrderByDescending(r => r.IsDirectory)
                    .ThenByDescending(r => r.FolderLogical)
                    .ThenByDescending(r => r.StreamLogical);
        var complete = ordered.ThenBy(r => r.RecordIndex).ThenBy(r => r.Ordinal);
        var rows = (
            pageSize is { } size ? complete.Skip(offset).Take(checked(size + 1)) : complete
        ).ToArray();
        var entries = ImmutableArray.CreateBuilder<FolderEntry>();
        foreach (var row in rows.Take(pageSize ?? rows.Length))
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
                {
                    StandardFileAttributes = row.StandardFileAttributes,
                }
            );
        }
        return new(entries.ToImmutable(), pageSize is { } limit && rows.Length > limit, totalCount);
    }
}
