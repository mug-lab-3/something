using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Something.Storage;

namespace Something.StorageCompare;

/// <summary>Benchmark-only equivalent of DuckDB's derived browsing table.</summary>
internal static class SqliteBrowser
{
    internal static void Create(ScanDbContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        context.Database.ExecuteSqlRaw("CREATE TABLE browser AS " + Select + " WHERE 0;");
        token.ThrowIfCancellationRequested();
        context.Database.ExecuteSqlRaw(
            """
            CREATE UNIQUE INDEX browser_identity ON browser(ScanId,RecordIndex,Ordinal);
            CREATE INDEX browser_parent ON browser(
                ScanId,ParentIndex,ParentSequence,IsDirectory DESC,
                EntrySubtreeAllocated DESC,StreamAllocated DESC,RecordIndex,Ordinal);
            """
        );
    }

    internal static void Append(ScanDbContext context, Guid scanId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText =
            "INSERT INTO browser "
            + Select
            + """
                 WHERE n.ScanId=$scan AND n.Namespace<>2 AND o.Domain=0 AND o.RecordIndex>=16
                   AND o.Sequence IS NOT NULL AND o.IsDirectory IS NOT NULL
                 ORDER BY n.ParentIndex,n.ParentSequence,n.RecordIndex,n.Ordinal;
                """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$scan";
        parameter.Value = scanId.ToString("D").ToUpperInvariant();
        command.Parameters.Add(parameter);
        command.ExecuteNonQuery();
        token.ThrowIfCancellationRequested();
    }

    private const string Select =
        """
            SELECT n.ScanId,n.ParentIndex,n.ParentSequence,n.RecordIndex,n.Ordinal,n.Name,
                o.Sequence,o.IsDirectory,o.Resolution,o.LinksComplete,
                f.EntrySubtreeLogical,f.EntrySubtreeAllocated,f.Sequence AS FolderSequence,
                f.EntrySubtreeUnresolved,f.UnobservedDirectories,s.Logical AS StreamLogical,s.Allocated AS StreamAllocated
            FROM file_name n JOIN observation o ON o.ScanId=n.ScanId AND o.RecordIndex=n.RecordIndex
            LEFT JOIN folder_sample f ON f.ScanId=n.ScanId AND f.RecordIndex=n.RecordIndex
            LEFT JOIN stream s ON s.ScanId=n.ScanId AND s.RecordIndex=n.RecordIndex AND length(s.Name)=0
            """ + "\n";
}
