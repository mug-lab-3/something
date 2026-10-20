using System.Numerics;

namespace Something.Application.Export;

public enum ExportFormat
{
    PlainText,
    Html,
    Csv,
    Tsv,
    Json,
    Parquet,
}

/// <summary>How a column is stored for tools and shown to people.</summary>
public enum ExportValueKind
{
    Text,

    /// <summary>A byte count; signed for size changes.</summary>
    Bytes,
    Count,
    Boolean,
}

/// <param name="Key">Stable field name for CSV, TSV, JSON and Parquet.</param>
/// <param name="Title">Localized heading for plain text and HTML.</param>
public sealed record ExportColumn(string Key, string Title, ExportValueKind Kind);

/// <param name="Key">Stable metadata name for JSON and Parquet.</param>
/// <param name="Title">Localized label for plain text and HTML.</param>
/// <param name="Value">Value for tools; empty when not set.</param>
/// <param name="DisplayValue">Localized value for plain text and HTML when it differs.</param>
public sealed record ExportProperty(
    string Key,
    string Title,
    string Value,
    string? DisplayValue = null
)
{
    public string Display => DisplayValue ?? Value;
}

/// <summary>
/// One exported table. Values are <see cref="string"/>, <see cref="bool"/>, integer types,
/// <see cref="UInt128"/> or <see cref="BigInteger"/>, or null when unknown. Language is the
/// BCP 47 tag of the titles and labels, such as "ja-JP".
/// </summary>
public sealed record ExportTable(
    string Title,
    IReadOnlyList<ExportProperty> Properties,
    IReadOnlyList<ExportColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    string Language = "ja-JP"
)
{
    public const string FormatName = "something-export";
    public const int FormatVersion = 1;

    public static string Extension(ExportFormat format) =>
        format switch
        {
            ExportFormat.PlainText => ".txt",
            ExportFormat.Html => ".html",
            ExportFormat.Csv => ".csv",
            ExportFormat.Tsv => ".tsv",
            ExportFormat.Json => ".json",
            ExportFormat.Parquet => ".parquet",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    internal static BigInteger? Integer(object? value) =>
        value switch
        {
            null => null,
            BigInteger number => number,
            UInt128 number => number,
            ulong number => number,
            long number => number,
            uint number => number,
            int number => number,
            _ => throw new InvalidDataException($"Not an integer export value: {value.GetType()}."),
        };
}
