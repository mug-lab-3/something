using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Something.Storage;

namespace Something.StorageCompare;

internal static class Workloads
{
    private const string Joins =
        """
            FROM file_name n
            JOIN observation o ON o.ScanId=n.ScanId AND o.RecordIndex=n.RecordIndex
            LEFT JOIN folder_sample f ON f.ScanId=n.ScanId AND f.RecordIndex=n.RecordIndex
            LEFT JOIN stream s ON s.ScanId=n.ScanId AND s.RecordIndex=n.RecordIndex AND s.Name=$empty
            WHERE n.ScanId=$latest AND n.ParentIndex=$parent AND n.ParentSequence=$sequence
              AND n.Namespace<>2 AND o.Sequence IS NOT NULL AND o.IsDirectory IS NOT NULL AND o.Domain=0 AND o.RecordIndex>=16
            """ + "\n";
    private const string Entries =
        """
            SELECT n.RecordIndex, n.Ordinal, n.Name, o.Sequence, o.IsDirectory, o.Resolution, o.LinksComplete,
              f.EntrySubtreeLogical, f.EntrySubtreeAllocated, f.Sequence, f.EntrySubtreeUnresolved, f.UnobservedDirectories,
              s.Logical, s.Allocated
            """ + "\n";
    private const string Ordering =
        " ORDER BY o.IsDirectory DESC,f.EntrySubtreeAllocated DESC,s.Allocated DESC,n.RecordIndex,n.Ordinal LIMIT 251 ";

    internal static QueryResult[] Measure(
        DbConnection connection,
        Guid[] ids,
        int count,
        CancellationToken token,
        bool native = false,
        bool browser = false
    )
    {
        List<(string Name, string Sql, long Parent, long Sequence)> cases =
        [
            (
                "root-tree",
                Entries + Joins + " AND o.IsDirectory=1 AND f.RecordIndex IS NOT NULL" + Ordering,
                5,
                1
            ),
            ("root-first-page", Entries + Joins + Ordering, 5, 1),
            ("root-next-page", Entries + Joins + Ordering + " OFFSET 250", 5, 1),
            ("root-count", "SELECT COUNT(*) " + Joins, 5, 1),
            ("small-folder", Entries + Joins + Ordering, 32, 1),
            (
                "capacity-top20",
                "SELECT RecordIndex,Sequence,EntrySubtreeAllocated,EntrySubtreeUnresolved FROM folder_sample WHERE ScanId=$latest AND RecordIndex<>5 ORDER BY EntrySubtreeAllocated DESC,RecordIndex LIMIT 20",
                5,
                1
            ),
            (
                "history-delta-top20",
                """
                SELECT a.RecordIndex,a.Sequence,a.EntrySubtreeAllocated,b.EntrySubtreeAllocated
                FROM folder_sample a JOIN folder_sample b ON a.RecordIndex=b.RecordIndex AND a.Sequence=b.Sequence
                WHERE a.ScanId=$baseline AND b.ScanId=$latest AND a.RecordIndex<>5
                  AND a.EntrySubtreeUnresolved=0 AND b.EntrySubtreeUnresolved=0
                  AND a.DirectoryObserved=1 AND b.DirectoryObserved=1
                  AND a.UnobservedDirectories=0 AND b.UnobservedDirectories=0
                ORDER BY a.RecordIndex
                """,
                5,
                1
            ),
        ];
        if (native || browser)
        {
            const string select =
                "SELECT RecordIndex,Ordinal,Name,Sequence,IsDirectory,Resolution,LinksComplete,EntrySubtreeLogical,EntrySubtreeAllocated,FolderSequence,EntrySubtreeUnresolved,UnobservedDirectories,StreamLogical,StreamAllocated ";
            const string where =
                "FROM browser WHERE ScanId=$latest AND ParentIndex=$parent AND ParentSequence=$sequence ";
            const string order =
                " ORDER BY IsDirectory DESC,EntrySubtreeAllocated DESC,StreamAllocated DESC,RecordIndex,Ordinal LIMIT 251 ";
            cases[0] = (
                "root-tree",
                select + where + " AND IsDirectory=1 AND FolderSequence IS NOT NULL" + order,
                5,
                1
            );
            cases[1] = ("root-first-page", select + where + order, 5, 1);
            cases[2] = ("root-next-page", select + where + order + " OFFSET 250", 5, 1);
            cases[3] = ("root-count", "SELECT COUNT(*) " + where, 5, 1);
            cases[4] = ("small-folder", select + where + order, 32, 1);
        }
        if (native)
        {
            cases[^1] = (
                "history-delta-top20",
                """
                SELECT a.RecordIndex,a.Sequence,b.EntrySubtreeAllocated-a.EntrySubtreeAllocated AS Delta
                FROM folder_sample a JOIN folder_sample b ON a.RecordIndex=b.RecordIndex AND a.Sequence=b.Sequence
                WHERE a.ScanId=$baseline AND b.ScanId=$latest AND a.RecordIndex<>5
                    AND a.EntrySubtreeUnresolved=0 AND b.EntrySubtreeUnresolved=0
                    AND a.DirectoryObserved=1 AND b.DirectoryObserved=1
                    AND a.UnobservedDirectories=0 AND b.UnobservedDirectories=0
                    AND b.EntrySubtreeAllocated>a.EntrySubtreeAllocated
                ORDER BY Delta DESC,a.RecordIndex LIMIT 20
                """,
                5,
                1
            );
        }
        var results = new List<QueryResult>();
        foreach (var item in cases)
        {
            using var command = connection.CreateCommand();
            command.CommandText = item.Sql;
            Add(command, "$latest", ids[1].ToString("D").ToUpperInvariant());
            Add(command, "$baseline", ids[0].ToString("D").ToUpperInvariant());
            Add(command, "$parent", item.Parent);
            Add(command, "$sequence", item.Sequence);
            Add(command, "$empty", Array.Empty<byte>());
            var watch = Stopwatch.StartNew();
            ulong expected = Consume(
                command,
                item.Name.StartsWith("history-", StringComparison.Ordinal),
                token
            );
            double first = watch.Elapsed.TotalMilliseconds;
            var times = new double[15];
            for (int repeat = 0; repeat < times.Length; repeat++)
            {
                watch.Restart();
                ulong actual = Consume(
                    command,
                    item.Name.StartsWith("history-", StringComparison.Ordinal),
                    token
                );
                times[repeat] = watch.Elapsed.TotalMilliseconds;
                if (actual != expected)
                    throw new InvalidDataException(
                        "Query result changed during synthetic benchmark."
                    );
            }
            Array.Sort(times);
            results.Add(new(item.Name, first, times[times.Length / 2], times[^1], expected));
        }
        return [.. results];
    }

    private static ulong Consume(DbCommand command, bool delta, CancellationToken token)
    {
        using var reader = command.ExecuteReader();
        ulong hash = 14695981039346656037;
        var differences = new List<(long Index, long Sequence, BigInteger Delta)>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (delta)
            {
                differences.Add(
                    (
                        Convert.ToInt64(Value(reader, 0)),
                        Convert.ToInt64(Value(reader, 1)),
                        reader.FieldCount == 3
                            ? BigInteger.CreateChecked(
                                StorageEncoding.Unsigned128((byte[])Value(reader, 2))
                            )
                            : BigInteger.CreateChecked(
                                StorageEncoding.Unsigned128((byte[])Value(reader, 3))
                            )
                                - BigInteger.CreateChecked(
                                    StorageEncoding.Unsigned128((byte[])Value(reader, 2))
                                )
                    )
                );
            }
            else
                for (int i = 0; i < reader.FieldCount; i++)
                    Mix(ref hash, Value(reader, i));
        }
        if (delta)
            foreach (
                var row in differences
                    .Where(row => row.Delta > 0)
                    .OrderByDescending(row => row.Delta)
                    .ThenBy(row => row.Index)
                    .Take(20)
            )
            {
                Mix(ref hash, row.Index);
                Mix(ref hash, row.Sequence);
                Mix(ref hash, row.Delta.ToByteArray());
            }
        return hash;
    }

    private static void Mix(ref ulong hash, object value)
    {
        byte[] data = value switch
        {
            DBNull => [0],
            byte[] bytes => bytes,
            string text => Encoding.UTF8.GetBytes(text),
            _ => BitConverter.GetBytes(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        };
        foreach (byte b in data)
            hash = unchecked((hash ^ b) * 1099511628211);
        hash = unchecked((hash ^ 255) * 1099511628211);
    }

    // Full canonical SHA-256 over every persisted table/column. Outside the timed workloads.
    internal static string Fingerprint(DbConnection connection, Guid[] ids, CancellationToken token)
    {
        using var context = new ScanDbContextFactory().CreateDbContext([]);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (
            var entity in context
                .Model.GetEntityTypes()
                .OrderBy(entity => entity.GetTableName(), StringComparer.Ordinal)
        )
        {
            var properties = entity.GetProperties().ToArray();
            AddText(entity.GetTableName()!);
            foreach (var property in properties)
                AddText(property.GetColumnName());
            string idColumn = properties.Any(property => property.Name == "ScanId")
                ? "ScanId"
                : "Id";
            for (int generation = 0; generation < ids.Length; generation++)
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT {string.Join(',', properties.Select(property => DuckStore.Quote(property.GetColumnName())))} FROM {DuckStore.Quote(entity.GetTableName()!)} WHERE {DuckStore.Quote(idColumn)}=$id ORDER BY {string.Join(',', entity.FindPrimaryKey()!.Properties.Select(property => DuckStore.Quote(property.GetColumnName())))}";
                Add(command, "$id", ids[generation].ToString("D").ToUpperInvariant());
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    hash.AppendData([1]);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        object value = Value(reader, i);
                        byte[] bytes = value switch
                        {
                            DBNull => [],
                            byte[] blob => blob,
                            string text
                                when Guid.TryParse(text, out var guid)
                                    && Array.IndexOf(ids, guid) is >= 0 and var index =>
                                Encoding.UTF8.GetBytes($"generation-{index}"),
                            string text => Encoding.UTF8.GetBytes(text),
                            _ => BitConverter.GetBytes(
                                Convert.ToInt64(value, CultureInfo.InvariantCulture)
                            ),
                        };
                        hash.AppendData([value is DBNull ? (byte)0 : (byte)1]);
                        hash.AppendData(BitConverter.GetBytes(bytes.Length));
                        hash.AppendData(bytes);
                    }
                }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());

        void AddText(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
    }

    // Compare every visible browsing row, including rows beyond measured pages.
    // Engines without a read model produce the equivalent rows from source facts.
    internal static string BrowserFingerprint(
        DbConnection connection,
        Guid[] ids,
        CancellationToken token,
        bool browser
    )
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int generation = 0; generation < ids.Length; generation++)
        {
            using var command = connection.CreateCommand();
            command.CommandText = browser
                ? """
                    SELECT ParentIndex,ParentSequence,RecordIndex,Ordinal,Name,Sequence,IsDirectory,Resolution,LinksComplete,
                        EntrySubtreeLogical,EntrySubtreeAllocated,FolderSequence,EntrySubtreeUnresolved,UnobservedDirectories,StreamLogical,StreamAllocated
                    FROM browser WHERE ScanId=$latest ORDER BY RecordIndex,Ordinal
                    """
                : "SELECT n.ParentIndex,n.ParentSequence,"
                    + Entries[7..]
                    + Joins.Replace(
                        " AND n.ParentIndex=$parent AND n.ParentSequence=$sequence",
                        "",
                        StringComparison.Ordinal
                    )
                    + " ORDER BY n.RecordIndex,n.Ordinal";
            Add(command, "$latest", ids[generation].ToString("D").ToUpperInvariant());
            Add(command, "$empty", Array.Empty<byte>());
            hash.AppendData(BitConverter.GetBytes(generation));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData([1]);
                for (int ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    object value = Value(reader, ordinal);
                    byte[] bytes = value switch
                    {
                        DBNull => [],
                        byte[] blob => blob,
                        _ => BitConverter.GetBytes(
                            Convert.ToInt64(value, CultureInfo.InvariantCulture)
                        ),
                    };
                    hash.AppendData([value is DBNull ? (byte)0 : (byte)1]);
                    hash.AppendData(BitConverter.GetBytes(bytes.Length));
                    hash.AppendData(bytes);
                }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static object Value(DbDataReader reader, int ordinal)
    {
        object value = reader.GetValue(ordinal);
        if (value is Guid guid)
            return guid.ToString("D").ToUpperInvariant();
        if (value is BigInteger integer)
            return StorageEncoding.Unsigned128(UInt128.CreateChecked(integer));
        if (value is not Stream stream)
            return value;
        using (stream)
        {
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return bytes;
        }
    }

    private static void Add(DbCommand command, string name, object value)
    {
        if (!command.CommandText.Contains(name, StringComparison.Ordinal))
            return;
        var parameter = command.CreateParameter();
        parameter.ParameterName = command is DuckDB.NET.Data.DuckDBCommand ? name[1..] : name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
