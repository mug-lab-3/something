using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Something.Storage;

// Set comparisons are exact, including UTF-16 bytes, ordinal, aliases, streams and uncertainty.
// Incoming facts are TEMP tables: they are never an intermediate published generation.
internal static class SharedHistoryPublication
{
    internal const int LargeListingThreshold = 4096;
    internal const int BlockRecordRange = 1024;
    private static readonly string[] FactTables =
    [
        "observation",
        "file_name",
        "stream",
        "folder_sample",
    ];

    internal static void Prepare(SharedHistoryDbContext context, CancellationToken token)
    {
        foreach (string table in FactTables)
        {
            token.ThrowIfCancellationRequested();
            Execute(
                context,
                $"DROP TABLE IF EXISTS incoming_{table}; CREATE TEMP TABLE incoming_{table} AS SELECT * FROM state_{table} WHERE FALSE;"
            );
        }
    }

    internal static void Publish(
        SharedHistoryDbContext context,
        ScanEntity scan,
        CancellationToken token,
        ScanWorkReporter reporter
    )
    {
        var prior = context
            .Published.AsNoTracking()
            .Where(p =>
                p.VolumeKey == scan.VolumeKey
                && p.RangeStart == scan.RangeStart
                && p.RangeCount == scan.RangeCount
            )
            .Join(context.Scans.AsNoTracking(), p => p.ScanId, s => s.Id, (p, s) => s)
            .SingleOrDefault();
        Guid previous =
            prior is { State: StoredScanState.Completed }
            && prior.Serial == scan.Serial
            && prior.AttributionPolicy == scan.AttributionPolicy
            && prior.CapacityPolicy == scan.CapacityPolicy
                ? prior.Id
                : Guid.Empty;
        string id = Literal(scan.Id),
            old = Literal(previous);
        reporter.Start(ScanWorkStage.ValidatingRelationships);
        token.ThrowIfCancellationRequested();
        Execute(
            context,
            "DROP TABLE IF EXISTS changed_fact; DROP TABLE IF EXISTS incoming_ref; DROP TABLE IF EXISTS changed_sample; DROP TABLE IF EXISTS incoming_entries; DROP TABLE IF EXISTS incoming_listing; DROP TABLE IF EXISTS changed_listing;"
        );
        var differences = new List<string>();
        foreach (string table in FactTables.Take(3))
        {
            string columns = Columns(context, table);
            differences.Add(
                $"SELECT RecordIndex FROM (SELECT {columns} FROM incoming_{table} EXCEPT SELECT {columns} FROM {table} WHERE ScanId={old})"
            );
            differences.Add(
                $"SELECT RecordIndex FROM (SELECT {columns} FROM {table} WHERE ScanId={old} EXCEPT SELECT {columns} FROM incoming_{table})"
            );
        }
        Execute(
            context,
            $"CREATE TEMP TABLE changed_fact AS {string.Join(" UNION ", differences)};"
        );
        Execute(
            context,
            $"""
            CREATE TEMP TABLE incoming_ref AS
            SELECT n.RecordIndex,CASE WHEN c.RecordIndex IS NULL AND p.VersionId IS NOT NULL THEN p.VersionId ELSE {id} END VersionId
            FROM incoming_observation n LEFT JOIN changed_fact c ON c.RecordIndex=n.RecordIndex
            LEFT JOIN observation_ref p ON p.ScanId={old} AND p.RecordIndex=n.RecordIndex;
            """
        );
        foreach (string table in FactTables.Take(3))
        {
            token.ThrowIfCancellationRequested();
            Execute(
                context,
                $"INSERT INTO state_{table} SELECT n.* FROM incoming_{table} n JOIN incoming_ref r ON r.RecordIndex=n.RecordIndex WHERE r.VersionId={id};"
            );
        }
        string sampleColumns = Columns(context, "folder_sample");
        Execute(
            context,
            $"""
            CREATE TEMP TABLE changed_sample AS SELECT RecordIndex FROM (
                SELECT {sampleColumns} FROM incoming_folder_sample EXCEPT SELECT {sampleColumns} FROM folder_sample WHERE ScanId={old});
            INSERT INTO scan_folder_ref (ScanId,RecordIndex,Sequence,ObservationVersion,SampleVersion)
            SELECT {id},n.RecordIndex,n.Sequence,r.VersionId,
                CASE WHEN c.RecordIndex IS NULL AND p.SampleVersion IS NOT NULL THEN p.SampleVersion ELSE {id} END
            FROM incoming_folder_sample n LEFT JOIN changed_sample c ON c.RecordIndex=n.RecordIndex
            LEFT JOIN scan_folder_ref p ON p.ScanId={old} AND p.RecordIndex=n.RecordIndex
            LEFT JOIN incoming_ref r ON r.RecordIndex=n.RecordIndex;
            INSERT INTO state_folder_sample SELECT n.* FROM incoming_folder_sample n
            JOIN scan_folder_ref r ON r.ScanId={id} AND r.RecordIndex=n.RecordIndex WHERE r.SampleVersion={id};
            """
        );
        token.ThrowIfCancellationRequested();
        reporter.Start(ScanWorkStage.BuildingFolderList);
        Execute(
            context,
            $"""
            CREATE TEMP TABLE incoming_entries AS
            SELECT n.ParentIndex,n.ParentSequence,
                CASE WHEN count(*) OVER(PARTITION BY n.ParentIndex,n.ParentSequence)>{LargeListingThreshold}
                     THEN n.RecordIndex // {BlockRecordRange}+1 ELSE 0 END Bucket,
                n.RecordIndex,n.Ordinal,r.VersionId ObservationVersion,n.Name,o.Sequence,o.IsDirectory,o.Resolution,o.LinksComplete,o.StandardFileAttributes,
                s.Logical StreamLogical,s.Allocated StreamAllocated
            FROM incoming_file_name n JOIN incoming_observation o ON o.RecordIndex=n.RecordIndex
            JOIN incoming_ref r ON r.RecordIndex=n.RecordIndex
            LEFT JOIN incoming_stream s ON s.RecordIndex=n.RecordIndex AND octet_length(s.Name)=0
            WHERE n.Namespace<>2 AND o.Domain=0 AND o.RecordIndex>=16 AND o.Sequence IS NOT NULL AND o.IsDirectory IS NOT NULL;
            CREATE TEMP TABLE incoming_listing AS SELECT DISTINCT ParentIndex,ParentSequence,Bucket FROM incoming_entries;
            CREATE TEMP TABLE changed_listing AS
            SELECT DISTINCT ParentIndex,ParentSequence,Bucket FROM (
                (SELECT ParentIndex,ParentSequence,Bucket,RecordIndex,Ordinal,ObservationVersion FROM incoming_entries
                 EXCEPT SELECT m.ParentIndex,m.ParentSequence,m.Bucket,e.RecordIndex,e.Ordinal,e.ObservationVersion
                 FROM scan_listing_ref m JOIN shared_entry e ON e.ScanId=m.ListingId WHERE m.ScanId={old})
                UNION ALL
                (SELECT m.ParentIndex,m.ParentSequence,m.Bucket,e.RecordIndex,e.Ordinal,e.ObservationVersion
                 FROM scan_listing_ref m JOIN shared_entry e ON e.ScanId=m.ListingId WHERE m.ScanId={old}
                 EXCEPT SELECT ParentIndex,ParentSequence,Bucket,RecordIndex,Ordinal,ObservationVersion FROM incoming_entries));
            INSERT INTO scan_listing_ref (ScanId,ParentIndex,ParentSequence,Bucket,ListingId)
            SELECT {id},n.ParentIndex,n.ParentSequence,n.Bucket,
                CASE WHEN c.ParentIndex IS NULL AND p.ListingId IS NOT NULL THEN p.ListingId ELSE uuid() END
            FROM incoming_listing n LEFT JOIN changed_listing c USING(ParentIndex,ParentSequence,Bucket)
            LEFT JOIN scan_listing_ref p ON p.ScanId={old} AND p.ParentIndex=n.ParentIndex AND p.ParentSequence=n.ParentSequence AND p.Bucket=n.Bucket;
            INSERT INTO shared_entry (ScanId,RecordIndex,Ordinal,ObservationVersion,Name,Sequence,IsDirectory,Resolution,LinksComplete,StandardFileAttributes,StreamLogical,StreamAllocated)
            SELECT m.ListingId,e.* EXCLUDE(ParentIndex,ParentSequence,Bucket)
            FROM incoming_entries e JOIN scan_listing_ref m ON m.ScanId={id} AND m.ParentIndex=e.ParentIndex AND m.ParentSequence=e.ParentSequence AND m.Bucket=e.Bucket
            WHERE NOT EXISTS (SELECT 1 FROM scan_listing_ref p WHERE p.ScanId={old} AND p.ListingId=m.ListingId);
            INSERT INTO auxiliary_ref (ScanId,RecordIndex,VersionId)
            SELECT {id},r.RecordIndex,r.VersionId FROM incoming_ref r
            WHERE NOT EXISTS(SELECT 1 FROM incoming_entries e WHERE e.RecordIndex=r.RecordIndex)
                AND NOT EXISTS(SELECT 1 FROM scan_folder_ref f WHERE f.ScanId={id} AND f.RecordIndex=r.RecordIndex AND f.ObservationVersion IS NOT NULL);
            """
        );
        token.ThrowIfCancellationRequested();
        using (var restored = context.Database.GetDbConnection().CreateCommand())
        {
            restored.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
            restored.CommandText =
                $"SELECT count(*) FROM (SELECT RecordIndex,VersionId FROM incoming_ref EXCEPT SELECT RecordIndex,VersionId FROM observation_ref WHERE ScanId={id})";
            if (Convert.ToInt64(restored.ExecuteScalar()) != 0)
                throw new InvalidDataException(
                    "Shared history did not preserve every incoming observation."
                );
        }
        foreach (string table in FactTables)
            Execute(context, $"DROP TABLE incoming_{table};");
        Execute(
            context,
            "DROP TABLE changed_fact; DROP TABLE incoming_ref; DROP TABLE changed_sample; DROP TABLE incoming_entries; DROP TABLE incoming_listing; DROP TABLE changed_listing;"
        );
    }

    private static string Columns(SharedHistoryDbContext context, string view) =>
        string.Join(
            ',',
            context
                .Model.GetEntityTypes()
                .Single(e => e.GetViewName() == view)
                .GetProperties()
                .Where(p => p.Name != "ScanId")
                .Select(p => $"\"{p.Name}\"")
        );

    private static string Literal(Guid id) => $"'{id:D}'::UUID";

    internal static void Execute(SharedHistoryDbContext context, string sql)
    {
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static void Validate(
        SharedHistoryDbContext context,
        CancellationToken token,
        ScanWorkReporter? reporter
    )
    {
        foreach (
            Guid id in context
                .ChangeTracker.Entries<ScanEntity>()
                .Select(e => e.Entity.Id)
                .Concat(context.DirtyGenerations)
                .Distinct()
        )
        {
            token.ThrowIfCancellationRequested();
            foreach (
                string sql in new[]
                {
                    $"SELECT 1 FROM scan_folder_ref r WHERE r.ScanId={Literal(id)} AND NOT EXISTS(SELECT 1 FROM state_folder_sample s WHERE s.ScanId=r.SampleVersion AND s.RecordIndex=r.RecordIndex AND s.Sequence=r.Sequence) LIMIT 1",
                    $"SELECT 1 FROM observation_ref r WHERE r.ScanId={Literal(id)} AND NOT EXISTS(SELECT 1 FROM state_observation s WHERE s.ScanId=r.VersionId AND s.RecordIndex=r.RecordIndex) LIMIT 1",
                    $"SELECT 1 FROM scan_listing_ref r WHERE r.ScanId={Literal(id)} AND NOT EXISTS(SELECT 1 FROM shared_entry e WHERE e.ScanId=r.ListingId) LIMIT 1",
                    $"SELECT RecordIndex FROM observation_ref WHERE ScanId={Literal(id)} GROUP BY RecordIndex HAVING count(*)<>1 LIMIT 1",
                }
            )
            {
                using var command = context.Database.GetDbConnection().CreateCommand();
                command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
                command.CommandText = sql;
                if (command.ExecuteScalar() is not null)
                    throw new InvalidDataException(
                        "Incomplete or ambiguous shared history relationships."
                    );
            }
        }
    }

    internal static void Collect(SharedHistoryDbContext context)
    {
        // Called only inside retention's transaction after removing unleased generation references.
        Execute(context, CollectSql);
    }

    internal const string CollectSql = """
        DELETE FROM shared_entry e WHERE NOT EXISTS(SELECT 1 FROM scan_listing_ref r WHERE r.ListingId=e.ScanId);
            DROP TABLE IF EXISTS live_shared_states;
            CREATE TEMP TABLE live_shared_states AS
                SELECT RecordIndex,ObservationVersion VersionId FROM shared_entry
                UNION SELECT RecordIndex,ObservationVersion FROM scan_folder_ref WHERE ObservationVersion IS NOT NULL
                UNION SELECT RecordIndex,VersionId FROM auxiliary_ref;
            DELETE FROM state_file_name s WHERE NOT EXISTS(SELECT 1 FROM live_shared_states r WHERE r.VersionId=s.ScanId AND r.RecordIndex=s.RecordIndex);
            DELETE FROM state_stream s WHERE NOT EXISTS(SELECT 1 FROM live_shared_states r WHERE r.VersionId=s.ScanId AND r.RecordIndex=s.RecordIndex);
            DELETE FROM state_observation s WHERE NOT EXISTS(SELECT 1 FROM live_shared_states r WHERE r.VersionId=s.ScanId AND r.RecordIndex=s.RecordIndex);
        DELETE FROM state_folder_sample s WHERE NOT EXISTS(SELECT 1 FROM scan_folder_ref r WHERE r.SampleVersion=s.ScanId AND r.RecordIndex=s.RecordIndex);
            DROP TABLE live_shared_states;
        """;
}
