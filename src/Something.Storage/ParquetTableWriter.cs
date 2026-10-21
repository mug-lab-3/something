using DuckDB.NET.Data;
using Something.Application.Export;

namespace Something.Storage;

/// <summary>Writes rows to a ZSTD-compressed Parquet file through an in-memory DuckDB.</summary>
internal sealed class ParquetTableWriter : IParquetTableWriter
{
    public static ParquetTableWriter Instance { get; } = new();

    // The table is small and temporary; it never touches the history database or spills to disk.
    private const string Settings =
        "SET memory_limit='512MB'; SET threads=2; SET temp_directory=''; SET preserve_insertion_order=true;";

    public void Write(
        string path,
        IReadOnlyList<ParquetColumn> columns,
        IEnumerable<object?[]> rows,
        IEnumerable<KeyValuePair<string, string>> metadata,
        CancellationToken token
    )
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The Parquet path must be absolute.", nameof(path));
        if (columns.Count == 0)
            throw new ArgumentException("At least one column is required.", nameof(columns));
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, Settings);
        Execute(
            connection,
            "CREATE TABLE export ("
                + string.Join(
                    ", ",
                    columns.Select(column =>
                        Identifier(column.Name)
                        + column.Type switch
                        {
                            ParquetColumnType.Text => " VARCHAR",
                            ParquetColumnType.Integer => " BIGINT",
                            ParquetColumnType.Boolean => " BOOLEAN",
                            _ => throw new ArgumentOutOfRangeException(nameof(columns)),
                        }
                    )
                )
                + ")"
        );
        using (var appender = connection.CreateAppender("export"))
            foreach (var values in rows)
            {
                token.ThrowIfCancellationRequested();
                if (values.Length != columns.Count)
                    throw new ArgumentException(
                        "Every row needs one value per column.",
                        nameof(rows)
                    );
                var row = appender.CreateRow();
                for (int index = 0; index < values.Length; index++)
                    switch (values[index])
                    {
                        case null:
                            row.AppendNullValue();
                            break;
                        case string text when columns[index].Type == ParquetColumnType.Text:
                            row.AppendValue(text);
                            break;
                        case long number when columns[index].Type == ParquetColumnType.Integer:
                            row.AppendValue(number);
                            break;
                        case bool flag when columns[index].Type == ParquetColumnType.Boolean:
                            row.AppendValue(flag);
                            break;
                        default:
                            throw new ArgumentException(
                                $"Value does not match Parquet column {columns[index].Name}.",
                                nameof(rows)
                            );
                    }
                row.EndRow();
            }
        token.ThrowIfCancellationRequested();
        string keyValues = string.Join(
            ", ",
            metadata.Select(pair => $"{Literal(pair.Key)}: {Literal(pair.Value)}")
        );
        Execute(
            connection,
            $"COPY export TO {Literal(path)} (FORMAT PARQUET, COMPRESSION ZSTD"
                + (keyValues.Length == 0 ? "" : $", KV_METADATA {{{keyValues}}}")
                + ")"
        );
    }

    private static void Execute(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Identifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static string Literal(string text) => "'" + text.Replace("'", "''") + "'";
}
