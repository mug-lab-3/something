namespace Something.Application.Export;

public enum ParquetColumnType
{
    Text,

    /// <summary>Signed 64-bit integer; Parquet has no 128-bit integer type.</summary>
    Integer,
    Boolean,
}

public sealed record ParquetColumn(string Name, ParquetColumnType Type);

/// <summary>Writes rows to a Parquet file. Runs on the calling thread.</summary>
public interface IParquetTableWriter
{
    void Write(
        string path,
        IReadOnlyList<ParquetColumn> columns,
        IEnumerable<object?[]> rows,
        IEnumerable<KeyValuePair<string, string>> metadata,
        CancellationToken token
    );
}
