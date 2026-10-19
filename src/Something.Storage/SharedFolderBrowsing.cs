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
        int generation = SharedHistoryPublication.GenerationOf(db, scanId);
        long[] lists = db
            .Listings.AsNoTracking()
            .Where(r =>
                r.Generation == generation
                && r.ParentIndex == parentIndex
                && r.ParentSequence == parent.SequenceNumber
            )
            .Select(r => r.Listing)
            .ToArray();
        var entries = db.Entries.AsNoTracking().Where(e => lists.Contains(e.Listing));
        // Sample states grow with history. Resolve the few versions this listing's folders use,
        // so zone maps skip every other version instead of probing the whole state table.
        var directories = entries.Where(e => e.IsDirectory).Select(e => e.RecordIndex);
        var references = db
            .SharedFolders.AsNoTracking()
            .Where(r => r.Generation == generation && directories.Contains(r.RecordIndex))
            .Select(r => new { r.RecordIndex, r.SampleVersion })
            .ToArray();
        int[] versions = [.. references.Select(r => r.SampleVersion).Distinct()];
        long[] records = [.. references.Select(r => r.RecordIndex)];
        var used = db
            .Samples.AsNoTracking()
            .Where(s => versions.Contains(s.Version) && records.Contains(s.RecordIndex));
        var folders =
            from e in entries
            join r in db.SharedFolders.AsNoTracking().Where(r => r.Generation == generation)
                on e.RecordIndex equals r.RecordIndex
                into referenced
            from r in referenced.DefaultIfEmpty()
            join s in used
                on new { Version = r.SampleVersion, r.RecordIndex } equals new
                {
                    s.Version,
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
                StandardFileAttributes = e.StandardFileAttributes,
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
