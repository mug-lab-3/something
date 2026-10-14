using Microsoft.EntityFrameworkCore;
using Something.Ntfs;

namespace Something.Storage;

public sealed partial class ScanStore
{
    // Select immutable listing IDs before reading entries. This avoids starting a folder
    // request from a view of every file in every generation. Sorting/paging remain LINQ.
    private IQueryable<BrowserEntity> SharedFolderRows(Guid scanId, FileReference parent)
    {
        var db = (SharedHistoryDbContext)context;
        long parentIndex = checked((long)parent.RecordIndex);
        Guid[] lists = db
            .Listings.AsNoTracking()
            .Where(r =>
                r.ScanId == scanId
                && r.ParentIndex == parentIndex
                && r.ParentSequence == parent.SequenceNumber
            )
            .Select(r => r.ListingId)
            .ToArray();
        var entries = db.Entries.AsNoTracking().Where(e => lists.Contains(e.ScanId));
        var folders =
            from e in entries
            join r in db.SharedFolders.AsNoTracking().Where(r => r.ScanId == scanId)
                on e.RecordIndex equals r.RecordIndex
                into references
            from r in references.DefaultIfEmpty()
            join s in db.Samples.AsNoTracking()
                on new { ScanId = r.SampleVersion, r.RecordIndex } equals new
                {
                    s.ScanId,
                    s.RecordIndex,
                }
                into samples
            from s in samples.DefaultIfEmpty()
            select new BrowserEntity
            {
                ScanId = scanId,
                ParentIndex = parentIndex,
                ParentSequence = parent.SequenceNumber,
                RecordIndex = e.RecordIndex,
                Ordinal = e.Ordinal,
                Name = e.Name,
                Sequence = e.Sequence,
                IsDirectory = e.IsDirectory,
                Resolution = e.Resolution,
                LinksComplete = e.LinksComplete,
                StreamLogical = e.StreamLogical,
                StreamAllocated = e.StreamAllocated,
                EntrySubtreeLogical = s.EntrySubtreeLogical,
                EntrySubtreeAllocated = s.EntrySubtreeAllocated,
                FolderSequence = (ushort?)s.Sequence,
                EntrySubtreeUnresolved = s.EntrySubtreeUnresolved,
                UnobservedDirectories = (long?)s.UnobservedDirectories,
            };
        return folders;
    }
}
