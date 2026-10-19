using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Something.Storage;

namespace Something.StorageCompare;

// Times the SQL behind application reads with a raw reader, separating engine time from
// EF materialization. Writes EXPLAIN ANALYZE plans next to the history database.
internal static class QueryProfile
{
    internal static void Run(string databasePath, Guid scanId, CancellationToken token)
    {
        string id = $"'{scanId:D}'::UUID";
        (string Name, string Sql)[] queries =
        [
            (
                "observation-ordered",
                $"SELECT * FROM observation WHERE ScanId={id} ORDER BY RecordIndex"
            ),
            ("observation", $"SELECT * FROM observation WHERE ScanId={id}"),
            (
                "file-name-ordered",
                $"SELECT * FROM file_name WHERE ScanId={id} ORDER BY RecordIndex,Ordinal"
            ),
            ("file-name", $"SELECT * FROM file_name WHERE ScanId={id}"),
            (
                "stream-ordered",
                $"SELECT * FROM stream WHERE ScanId={id} ORDER BY RecordIndex,Ordinal"
            ),
            (
                "members",
                $"SELECT m.* FROM generation_observation m JOIN scan g ON g.Number=m.Generation WHERE g.Id={id}"
            ),
            ("folder-sample", $"SELECT * FROM folder_sample WHERE ScanId={id}"),
            (
                "folder-sample-halves",
                $"SELECT RecordIndex,(SubtreeAllocated>>64)::UBIGINT,(SubtreeAllocated&18446744073709551615::UHUGEINT)::UBIGINT FROM folder_sample WHERE ScanId={id}"
            ),
            (
                "folder-sample-narrow",
                $"SELECT RecordIndex,Sequence FROM folder_sample WHERE ScanId={id}"
            ),
            (
                "folder-ref",
                $"SELECT r.* FROM scan_folder_ref r JOIN scan g ON g.Number=r.Generation WHERE g.Id={id}"
            ),
            ("state-folder-sample-count", "SELECT count(*) FROM state_folder_sample"),
            ("folder-name-history", $"SELECT * FROM folder_name_history WHERE ScanId={id}"),
        ];
        using var store = ScanStore.OpenSharedHistoryReader(databasePath, token);
        var connection = store.HistoryContext.Database.GetDbConnection();
        string plans = Path.ChangeExtension(databasePath, ".plans.txt");
        using var writer = new StreamWriter(plans);
        foreach (var (name, sql) in queries)
        {
            token.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var watch = Stopwatch.StartNew();
            long rows = 0;
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    for (int i = 0; i < reader.FieldCount; i++)
                        _ = reader.GetValue(i);
                    rows++;
                }
            Console.WriteLine($"  sql {name}: {watch.Elapsed.TotalMilliseconds:F0}ms; rows={rows}");
            command.CommandText = "EXPLAIN ANALYZE " + sql;
            using var plan = command.ExecuteReader();
            writer.WriteLine($"==== {name}");
            while (plan.Read())
                writer.WriteLine(plan.GetValue(1));
        }
        Console.WriteLine($"  plans: {plans}");
    }
}
