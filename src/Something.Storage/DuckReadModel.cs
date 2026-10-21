using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Something.Application.Scanning;

namespace Something.Storage;

internal static class DuckReadModel
{
    // These measured set operations are isolated here; models and application queries remain EF/LINQ.
    // No physical foreign keys: immutable generation relationships are checked before publication.
    internal static void CompleteWrite(
        DuckScanDbContext context,
        CancellationToken token,
        ScanWorkReporter? progress = null
    )
    {
        progress?.Start(ScanWorkStage.ValidatingRelationships);
        foreach (var entry in context.ChangeTracker.Entries<ScanEntity>())
            context.DirtyGenerations.Add(entry.Entity.Id);
        foreach (Guid id in context.DirtyGenerations)
        {
            token.ThrowIfCancellationRequested();
            foreach (
                var (child, parent, key) in new (string, string, string)[]
                {
                    ("observation", "scan", "p.Id=c.ScanId"),
                    (
                        "file_name",
                        "observation",
                        "p.ScanId=c.ScanId AND p.RecordIndex=c.RecordIndex"
                    ),
                    ("stream", "observation", "p.ScanId=c.ScanId AND p.RecordIndex=c.RecordIndex"),
                    ("folder_sample", "scan", "p.Id=c.ScanId"),
                    (
                        "folder_name_history",
                        "folder_sample",
                        "p.ScanId=c.ScanId AND p.RecordIndex=c.RecordIndex"
                    ),
                    ("volume_sample", "scan", "p.Id=c.ScanId"),
                    ("published_generation", "scan", "p.Id=c.ScanId"),
                    ("scan_timing", "scan", "p.Id=c.ScanId"),
                    ("scan_step_timing", "scan_timing", "p.ScanId=c.ScanId"),
                }
            )
            {
                using var command = context.Database.GetDbConnection().CreateCommand();
                command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
                command.CommandText =
                    $"SELECT 1 FROM {child} c WHERE c.ScanId='{id:D}'::UUID AND NOT EXISTS (SELECT 1 FROM {parent} p WHERE {key}) LIMIT 1;";
                if (command.ExecuteScalar() is not null)
                    throw new InvalidDataException("Incomplete generation relationships.");
            }
            if (
                !context
                    .Scans.AsNoTracking()
                    .Any(s => s.Id == id && s.State == StoredScanState.Completed)
            )
                continue;
            progress?.Start(ScanWorkStage.BuildingFolderList);
            context.Database.ExecuteSqlInterpolated($"DELETE FROM browser WHERE ScanId={id}");
            context.Database.ExecuteSqlInterpolated(
                $"""
                INSERT INTO browser (ScanId,ParentIndex,ParentSequence,RecordIndex,Ordinal,Name,
                    Sequence,IsDirectory,Resolution,LinksComplete,StandardFileAttributes,EntrySubtreeLogical,EntrySubtreeAllocated,
                    FolderSequence,EntrySubtreeUnresolved,UnobservedDirectories,StreamLogical,StreamAllocated)
                SELECT n.ScanId,n.ParentIndex,n.ParentSequence,n.RecordIndex,n.Ordinal,n.Name,
                    o.Sequence,o.IsDirectory,o.Resolution,o.LinksComplete,o.StandardFileAttributes,f.EntrySubtreeLogical,f.EntrySubtreeAllocated,
                    f.Sequence,f.EntrySubtreeUnresolved,f.UnobservedDirectories,s.Logical,s.Allocated
                FROM file_name n JOIN observation o ON o.ScanId=n.ScanId AND o.RecordIndex=n.RecordIndex
                LEFT JOIN folder_sample f ON f.ScanId=n.ScanId AND f.RecordIndex=n.RecordIndex
                LEFT JOIN stream s ON s.ScanId=n.ScanId AND s.RecordIndex=n.RecordIndex AND octet_length(s.Name)=0
                WHERE n.ScanId={id} AND n.Namespace<>2 AND o.Domain=0 AND o.RecordIndex>=16
                    AND o.Sequence IS NOT NULL AND o.IsDirectory IS NOT NULL
                ORDER BY n.ParentIndex,n.ParentSequence,n.RecordIndex,n.Ordinal
                """
            );
            token.ThrowIfCancellationRequested();
        }
    }
}
