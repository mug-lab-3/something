using System.Data.Common;
using System.Globalization;
using System.Numerics;
using DuckDB.NET.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Something.Application.Scanning;
using Something.Storage;

namespace Something.StorageCompare;

// Experimental adapter only. Derives columns, conversions and constraints from the production EF model.
// Production continues to use SQLite/LINQ. DuckDB cascade deletion/partial indexes are not emulated here.
internal sealed class DuckStore : IDisposable
{
    internal DuckDBConnection Connection { get; }
    internal bool NativeSchema { get; }
    private readonly bool memory;
    private readonly string path;
    private bool browserCreated;
    private bool exported;
    private readonly List<string> schema = [];
    private readonly Dictionary<Type, Table> tables = [];

    internal DuckStore(string path, int budgetMiB, bool native = false, bool memory = false)
    {
        NativeSchema = native;
        this.memory = memory;
        this.path = path;
        Connection = new($"DataSource={(memory ? ":memory:" : path)}");
        Connection.Open();
        Execute(
            $"SET memory_limit='{budgetMiB}MiB'; SET threads=4; SET preserve_insertion_order=false;"
        );
        if (memory)
            Execute("ATTACH ':memory:' AS working (COMPRESS); USE working;");
        using var model = new ScanDbContextFactory().CreateDbContext([]);
        Type[] order =
        [
            typeof(ScanEntity),
            typeof(ObservationEntity),
            typeof(FileNameEntity),
            typeof(StreamEntity),
            typeof(FolderSampleEntity),
            typeof(FolderNameEntity),
            typeof(VolumeSampleEntity),
            typeof(PublishedGenerationEntity),
            typeof(HistorySnapshotEntity),
        ];
        foreach (var type in order)
        {
            var entity = model.GetService<IDesignTimeModel>().Model.FindEntityType(type)!;
            var table = new Table(entity, native);
            tables.Add(type, table);
            string sql =
                $"CREATE TABLE {Quote(table.Name)} ({string.Join(",", table.Columns.Select(column => $"{Quote(column.Name)} {column.SqlType}{(column.Nullable ? "" : " NOT NULL")}"))}, PRIMARY KEY ({Names(entity.FindPrimaryKey()!.Properties)} )";
            foreach (var constraint in entity.GetCheckConstraints())
            {
                string check = constraint.Sql;
                foreach (var column in table.Columns.Where(column => column.SqlType == "BLOB"))
                    check = check.Replace(
                        $"length({column.Name})",
                        $"octet_length({column.Name})",
                        StringComparison.Ordinal
                    );
                foreach (var column in table.Columns.Where(column => column.SqlType == "UHUGEINT"))
                    check = check.Replace(
                        $"length({column.Name})=16",
                        "TRUE",
                        StringComparison.Ordinal
                    );
                sql += $", CHECK ({check})";
            }
            foreach (var fk in entity.GetForeignKeys().Where(_ => !native))
                sql +=
                    $", FOREIGN KEY ({Names(fk.Properties)}) REFERENCES {Quote(fk.PrincipalEntityType.GetTableName()!)} ({Names(fk.PrincipalKey.Properties)})";
            schema.Add(sql + ");");
            Execute(schema[^1]);
            foreach (
                var index in entity
                    .GetIndexes()
                    .Where(index => index.GetFilter() is null && (!native || index.IsUnique))
            )
            {
                schema.Add(
                    $"CREATE {(index.IsUnique ? "UNIQUE " : "")}INDEX {Quote(index.GetDatabaseName()!)} ON {Quote(table.Name)} ({Names(index.Properties)});"
                );
                Execute(schema[^1]);
            }
        }
        if (native)
        {
            Execute("CREATE TEMP TABLE codec_probe (id UUID, capacity UHUGEINT, name BLOB);");
            Guid id = Guid.NewGuid();
            byte[] name = StorageEncoding.Name("test\ud800\0");
            using (var insert = Connection.CreateCommand())
            {
                insert.CommandText =
                    "INSERT INTO codec_probe VALUES ($id,CAST($capacity AS UHUGEINT),$name);";
                insert.Parameters.Add(new DuckDBParameter("id", id));
                insert.Parameters.Add(
                    new DuckDBParameter(
                        "capacity",
                        BigInteger
                            .CreateChecked(UInt128.MaxValue)
                            .ToString(CultureInfo.InvariantCulture)
                    )
                );
                insert.Parameters.Add(new DuckDBParameter("name", name));
                insert.ExecuteNonQuery();
            }
            using (var command = Connection.CreateCommand())
            {
                command.CommandText = "SELECT id,capacity,name FROM codec_probe;";
                using var reader = command.ExecuteReader();
                if (
                    !reader.Read()
                    || reader.GetGuid(0) != id
                    || reader.GetFieldValue<BigInteger>(1)
                        != BigInteger.CreateChecked(UInt128.MaxValue)
                )
                    throw new InvalidDataException("Native UUID/UInt128 codec lost information.");
                using var stream = reader.GetStream(2);
                byte[] actual = new byte[name.Length];
                stream.ReadExactly(actual);
                if (!actual.AsSpan().SequenceEqual(name))
                    throw new InvalidDataException("Native name codec lost UTF-16 code units.");
            }
            Execute("DROP TABLE codec_probe;");
            using var transaction = Connection.BeginTransaction();
            Append<FolderSampleEntity>(
                [
                    new()
                    {
                        ScanId = id,
                        RecordIndex = 0,
                        DirectLogical = UInt128.MaxValue,
                        DirectAllocated = UInt128.MaxValue,
                    },
                ],
                CancellationToken.None
            );
            using (var verify = Connection.CreateCommand())
            {
                verify.CommandText =
                    $"SELECT DirectLogical,DirectAllocated FROM folder_sample WHERE ScanId='{id:D}'::UUID;";
                using var reader = verify.ExecuteReader();
                if (
                    !reader.Read()
                    || reader.GetFieldValue<BigInteger>(0)
                        != BigInteger.CreateChecked(UInt128.MaxValue)
                    || reader.GetFieldValue<BigInteger>(1)
                        != BigInteger.CreateChecked(UInt128.MaxValue)
                )
                    throw new InvalidDataException("Full-range append path lost capacity.");
            }
            transaction.Rollback();
        }

        static string Names(IReadOnlyList<IProperty> properties) =>
            string.Join(',', properties.Select(property => Quote(property.GetColumnName())));
    }

    internal Guid Publish(
        BufferedScan buffer,
        int generation,
        CancellationToken token,
        Action? beforeCommit = null
    )
    {
        var scan = ScanStore.CreateScanEntity(buffer.Descriptor);
        scan.State = StoredScanState.Completed;
        scan.Ended = SyntheticData.End.AddDays(generation == 0 ? -7 : 0);
        using var transaction = Connection.BeginTransaction();
        Append<ScanEntity>([scan], token);
        Append(buffer.Observations.Select(row => ScanStore.ToEntity(scan.Id, row)), token);
        Append(
            buffer.Observations.SelectMany(row => ScanStore.ToEntity(scan.Id, row).Names),
            token
        );
        Append(
            buffer.Observations.SelectMany(row => ScanStore.ToEntity(scan.Id, row).Streams),
            token
        );
        Append(
            buffer.Aggregation.Folders.Values.Select(folder => ScanStore.ToEntity(scan.Id, folder)),
            token
        );
        Append(FolderNames(buffer, scan.Id), token);
        Append<VolumeSampleEntity>([ScanStore.ToEntity(scan.Id, buffer.Aggregation)], token);
        Execute("DELETE FROM published_generation;");
        Append<PublishedGenerationEntity>(
            [
                new()
                {
                    VolumeKey = scan.VolumeKey,
                    RangeStart = scan.RangeStart,
                    RangeCount = scan.RangeCount,
                    ScanId = scan.Id,
                },
            ],
            token
        );
        Append<HistorySnapshotEntity>(
            [
                new()
                {
                    Id = scan.Id,
                    VolumeKey = scan.VolumeKey,
                    Drive = "Synthetic",
                    CompletedUtcTicks = scan.Ended.Value.UtcTicks,
                    LocalDay = DateOnly.FromDateTime(scan.Ended.Value.UtcDateTime).DayNumber,
                },
            ],
            token
        );
        if (NativeSchema)
        {
            // Immutable generations are validated once with vectorized anti-joins rather than
            // maintaining an extra ART index for every foreign key during every row append.
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
                }
            )
            {
                using var check = Connection.CreateCommand();
                check.CommandText =
                    $"SELECT 1 FROM {Quote(child)} c WHERE c.ScanId='{scan.Id:D}'::UUID AND NOT EXISTS (SELECT 1 FROM {Quote(parent)} p WHERE {key}) LIMIT 1;";
                if (check.ExecuteScalar() is not null)
                    throw new InvalidDataException("Incomplete generation relationships.");
            }
            string select = $"""
                SELECT n.ScanId,n.ParentIndex,n.ParentSequence,n.RecordIndex,n.Ordinal,n.Name,
                    o.Sequence,o.IsDirectory,o.Resolution,o.LinksComplete,
                    f.EntrySubtreeLogical,f.EntrySubtreeAllocated,f.Sequence AS FolderSequence,
                    f.EntrySubtreeUnresolved,f.UnobservedDirectories,s.Logical AS StreamLogical,s.Allocated AS StreamAllocated
                FROM file_name n JOIN observation o ON o.ScanId=n.ScanId AND o.RecordIndex=n.RecordIndex
                LEFT JOIN folder_sample f ON f.ScanId=n.ScanId AND f.RecordIndex=n.RecordIndex
                LEFT JOIN stream s ON s.ScanId=n.ScanId AND s.RecordIndex=n.RecordIndex AND octet_length(s.Name)=0
                WHERE n.ScanId='{scan.Id:D}'::UUID AND n.Namespace<>2 AND o.Domain=0 AND o.RecordIndex>=16
                    AND o.Sequence IS NOT NULL AND o.IsDirectory IS NOT NULL
                ORDER BY n.ParentIndex,n.ParentSequence,n.RecordIndex,n.Ordinal
                """;
            Execute(
                browserCreated
                    ? "INSERT INTO browser " + select
                    : "CREATE TABLE browser AS " + select
            );
            if (!browserCreated)
                Execute("CREATE INDEX browser_parent ON browser(ParentIndex);");
            browserCreated = true;
        }
        beforeCommit?.Invoke();
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return scan.Id;
    }

    internal void WriteFile()
    {
        if (!memory)
        {
            Execute("CHECKPOINT;");
            return;
        }
        if (!exported)
        {
            if (File.Exists(path))
                throw new IOException("Export destination already exists.");
            Execute(
                $"ATTACH '{path.Replace("'", "''", StringComparison.Ordinal)}' AS persisted; COPY FROM DATABASE working TO persisted;"
            );
            exported = true;
        }
        else
        {
            using var transaction = Connection.BeginTransaction();
            foreach (var table in tables.Values)
            {
                if (table.Name == "published_generation")
                    Execute(
                        "DELETE FROM persisted.published_generation WHERE VolumeKey IN (SELECT VolumeKey FROM working.published_generation);"
                    );
                Execute(
                    $"INSERT INTO persisted.{Quote(table.Name)} SELECT * FROM working.{Quote(table.Name)};"
                );
            }
            if (NativeSchema)
                Execute("INSERT INTO persisted.browser SELECT * FROM working.browser;");
            transaction.Commit();
        }
        Execute("USE persisted; DETACH working; CHECKPOINT;");
    }

    internal void BeginNextGeneration()
    {
        if (!memory || !exported)
            return;
        Execute("ATTACH ':memory:' AS working (COMPRESS); USE working;");
        foreach (string command in schema)
            Execute(command);
        browserCreated = false;
    }

    private static IEnumerable<FolderNameEntity> FolderNames(BufferedScan buffer, Guid id)
    {
        foreach (var item in buffer.Observations)
        {
            if (
                item.File is not { IsDirectory: true } file
                || !buffer.Aggregation.Folders.ContainsKey(file.Reference)
            )
                continue;
            for (int i = 0; i < file.Names.Length; i++)
            {
                var name = file.Names[i];
                yield return new()
                {
                    ScanId = id,
                    RecordIndex = checked((long)file.Reference.RecordIndex),
                    Ordinal = i,
                    ParentIndex = checked((long)name.Parent.RecordIndex),
                    ParentSequence = name.Parent.SequenceNumber,
                    Name = name.Name,
                    Namespace = name.Namespace,
                    Attributes = name.FileAttributes,
                };
            }
        }
    }

    private void Append<T>(IEnumerable<T> rows, CancellationToken token)
        where T : class
    {
        var table = tables[typeof(T)];
        var appender = Connection.CreateAppender(table.Name);
        var largeValues = new List<(T Row, Column Column, BigInteger Value)>();
        try
        {
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                appender.AppendRow(
                    (table, row, largeValues),
                    static (writer, state) =>
                    {
                        foreach (var column in state.table.Columns)
                        {
                            var value = column.Read(state.row);
                            switch (value)
                            {
                                case null:
                                    writer.AppendNullValue();
                                    break;
                                case byte[] bytes:
                                    writer.AppendValue(bytes);
                                    break;
                                case string text:
                                    writer.AppendValue(text);
                                    break;
                                case Guid guid:
                                    writer.AppendValue(guid);
                                    break;
                                case BigInteger integer:
                                    if (integer > SignedMaximum)
                                    {
                                        state.largeValues.Add((state.row, column, integer));
                                        writer.AppendValue(BigInteger.Zero);
                                    }
                                    else
                                        writer.AppendValue(integer);
                                    break;
                                default:
                                    writer.AppendValue(
                                        Convert.ToInt64(value, CultureInfo.InvariantCulture)
                                    );
                                    break;
                            }
                        }
                    }
                );
            }
            appender.Dispose();
            foreach (var item in largeValues)
            {
                // This driver uses signed HUGEINT for BigInteger appends. Values above that
                // range are corrected within the same transaction, before publication.
                using var update = Connection.CreateCommand();
                update.CommandText =
                    $"UPDATE {Quote(table.Name)} SET {Quote(item.Column.Name)}=CAST($capacity AS UHUGEINT) WHERE {string.Join(" AND ", table.Keys.Select((column, index) => $"{Quote(column.Name)}=$key{index}"))};";
                update.Parameters.Add(
                    new DuckDBParameter(
                        "capacity",
                        item.Value.ToString(CultureInfo.InvariantCulture)
                    )
                );
                for (int i = 0; i < table.Keys.Length; i++)
                    update.Parameters.Add(
                        new DuckDBParameter($"key{i}", table.Keys[i].Read(item.Row))
                    );
                update.CommandText =
                    update.CommandText.TrimEnd(';') + $" RETURNING {Quote(item.Column.Name)};";
                if (update.ExecuteScalar() is not BigInteger actual || actual != item.Value)
                    throw new InvalidDataException("Full-range capacity correction failed.");
            }
        }
        catch
        {
            // A failed flush can abort the transaction. Preserve its original exception
            // even if disposing the appender then reports only "transaction aborted".
            try
            {
                appender.Dispose();
            }
            catch (DuckDBException) { }
            throw;
        }
    }

    internal void Execute(string sql)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static string Quote(string name) =>
        '"' + name.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    public void Dispose() => Connection.Dispose();

    private static readonly BigInteger SignedMaximum = (BigInteger.One << 127) - 1;

    private sealed class Table(IEntityType entity, bool native)
    {
        internal string Name { get; } = entity.GetTableName()!;
        internal Column[] Columns { get; } =
        [.. entity.GetProperties().Select(property => new Column(property, native))];
        internal Column[] Keys =>
            [
                .. entity
                    .FindPrimaryKey()!
                    .Properties.Select(property =>
                        Columns.Single(column => column.Name == property.GetColumnName())
                    ),
            ];
    }

    private sealed class Column
    {
        internal string Name { get; }
        internal string SqlType { get; }
        internal bool Nullable { get; }
        internal Func<object, object?> Read { get; }

        internal Column(IProperty property, bool native)
        {
            Name = property.GetColumnName();
            Nullable = property.IsNullable;
            var mapping = property.GetRelationalTypeMapping();
            var converter = mapping.Converter;
            var type =
                System.Nullable.GetUnderlyingType(converter?.ProviderClrType ?? property.ClrType)
                ?? converter?.ProviderClrType
                ?? property.ClrType;
            SqlType =
                type == typeof(byte[]) ? "BLOB"
                : type == typeof(string) || type == typeof(Guid) ? "VARCHAR"
                : "BIGINT";
            var clr = System.Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            if (native && clr == typeof(Guid))
                SqlType = "UUID";
            if (native && clr == typeof(UInt128))
                SqlType = "UHUGEINT";
            var getter = property.GetGetter();
            Guid? previous = null;
            string? previousText = null;
            Read = item =>
            {
                var value = getter.GetClrValue(item);
                if (value is null)
                    return null;
                if (native && value is UInt128 capacity)
                    return BigInteger.CreateChecked(capacity);
                if (native && value is Guid)
                    return value;
                var provider = converter is null ? value : converter.ConvertToProvider(value);
                if (provider is not Guid guid)
                    return provider;
                if (previous != guid)
                {
                    previous = guid;
                    previousText = guid.ToString("D").ToUpperInvariant();
                }
                return previousText;
            };
        }
    }
}
