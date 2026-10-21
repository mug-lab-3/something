using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Something.Application.Export;

/// <summary>
/// Writes an export table. CSV, TSV, JSON and Parquet keep raw values and stable keys for tools;
/// plain text and HTML use localized headings and the caller's display formatting for people.
/// The file is written beside the destination and then moved, so a failure never leaves a partial file.
/// </summary>
public static class TableExporter
{
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task WriteAsync(
        string path,
        ExportFormat format,
        ExportTable table,
        Func<ExportColumn, object?, string> display,
        CancellationToken token,
        IParquetTableWriter? parquet = null
    )
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(display);
        if (format == ExportFormat.Parquet)
            ArgumentNullException.ThrowIfNull(parquet);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The export path must be absolute.", nameof(path));
        foreach (var row in table.Rows)
            if (row.Length != table.Columns.Count)
                throw new ArgumentException("Every row needs one value per column.", nameof(table));
        string directory = Path.GetDirectoryName(path)!;
        string temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp"
        );
        try
        {
            if (format == ExportFormat.Parquet)
                await Task.Run(() => WriteParquet(parquet!, temporary, table, token), token);
            else
            {
                string text = format switch
                {
                    ExportFormat.PlainText => PlainText(table, display),
                    ExportFormat.Html => Html(table, display),
                    ExportFormat.Csv => Delimited(table, ',', Csv),
                    ExportFormat.Tsv => Delimited(table, '\t', Tsv),
                    ExportFormat.Json => Json(table),
                    _ => throw new ArgumentOutOfRangeException(nameof(format)),
                };
                // Excel reads UTF-8 delimited text correctly only with a byte order mark.
                var encoding = format is ExportFormat.Csv or ExportFormat.Tsv ? Utf8Bom : Utf8;
                await File.WriteAllTextAsync(temporary, text, encoding, token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal static string PlainText(ExportTable table, Func<ExportColumn, object?, string> display)
    {
        var builder = new StringBuilder();
        builder.Append(table.Title).Append("\r\n");
        foreach (var property in table.Properties)
            builder.Append(property.Title).Append(": ").Append(property.Display).Append("\r\n");
        builder.Append("\r\n");
        string[][] cells =
        [
            [.. table.Columns.Select(column => column.Title)],
            .. table.Rows.Select(row =>
                table
                    .Columns.Select((column, index) => SingleLine(display(column, row[index])))
                    .ToArray()
            ),
        ];
        int[] widths =
        [
            .. table.Columns.Select((_, index) => cells.Max(line => DisplayWidth(line[index]))),
        ];
        for (int line = 0; line < cells.Length; line++)
        {
            var parts = cells[line]
                .Select(
                    (cell, index) =>
                    {
                        string padding = new(' ', widths[index] - DisplayWidth(cell));
                        bool right = line > 0 && table.Columns[index].Kind != ExportValueKind.Text;
                        return right ? padding + cell : cell + padding;
                    }
                );
            builder.Append(string.Join("  ", parts).TrimEnd()).Append("\r\n");
            if (line == 0)
                builder
                    .Append(string.Join("  ", widths.Select(width => new string('-', width))))
                    .Append("\r\n");
        }
        return builder.ToString();
    }

    internal static string Html(ExportTable table, Func<ExportColumn, object?, string> display)
    {
        static string Encode(string text) => WebUtility.HtmlEncode(text);
        var builder = new StringBuilder();
        builder.Append(
            $"""
            <!DOCTYPE html>
            <html lang="{Encode(table.Language)}">
            <head>
            <meta charset="utf-8">
            <meta name="generator" content="{ExportTable.FormatName} {ExportTable.FormatVersion}">
            <title>{Encode(table.Title)}</title>
            <style>
            :root {"{"} color-scheme: light dark; font-family: "Segoe UI", "Yu Gothic UI", sans-serif; {"}"}
            body {"{"} margin: 24px; {"}"}
            dl {"{"} display: grid; grid-template-columns: max-content auto; gap: 4px 16px; {"}"}
            dt {"{"} font-weight: 600; {"}"}
            dd {"{"} margin: 0; {"}"}
            table {"{"} border-collapse: collapse; {"}"}
            th, td {"{"} padding: 4px 12px; border-bottom: 1px solid color-mix(in srgb, currentColor 20%, transparent); text-align: left; {"}"}
            th {"{"} position: sticky; top: 0; background: Canvas; {"}"}
            .number {"{"} text-align: right; font-variant-numeric: tabular-nums; {"}"}
            </style>
            </head>
            <body>
            <h1>{Encode(table.Title)}</h1>

            """
        );
        builder.Append("<dl>\n");
        foreach (var property in table.Properties)
            builder.Append(
                $"<dt>{Encode(property.Title)}</dt><dd>{Encode(property.Display)}</dd>\n"
            );
        builder.Append("</dl>\n<table>\n<thead>\n<tr>");
        foreach (var column in table.Columns)
            builder.Append($"<th{NumberClass(column)}>{Encode(column.Title)}</th>");
        builder.Append("</tr>\n</thead>\n<tbody>\n");
        foreach (var row in table.Rows)
        {
            builder.Append("<tr>");
            for (int index = 0; index < row.Length; index++)
            {
                var column = table.Columns[index];
                builder.Append(
                    $"<td{NumberClass(column)}>{Encode(display(column, row[index]))}</td>"
                );
            }
            builder.Append("</tr>\n");
        }
        builder.Append("</tbody>\n</table>\n</body>\n</html>\n");
        return builder.ToString();

        static string NumberClass(ExportColumn column) =>
            column.Kind == ExportValueKind.Text ? "" : " class=\"number\"";
    }

    internal static string Delimited(ExportTable table, char separator, Func<string, string> field)
    {
        var builder = new StringBuilder();
        builder
            .AppendJoin(separator, table.Columns.Select(column => field(column.Key)))
            .Append("\r\n");
        foreach (var row in table.Rows)
            builder.AppendJoin(separator, row.Select(value => field(Raw(value)))).Append("\r\n");
        return builder.ToString();
    }

    // RFC 4180: quote fields that contain a separator, quote or line break.
    internal static string Csv(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    // TSV has no quoting; tabs and line breaks inside a value would split the record.
    internal static string Tsv(string value) =>
        value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    internal static string Json(ExportTable table)
    {
        using var stream = new MemoryStream();
        using (
            var writer = new Utf8JsonWriter(
                stream,
                new JsonWriterOptions
                {
                    Indented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }
            )
        )
        {
            writer.WriteStartObject();
            writer.WriteString("format", ExportTable.FormatName);
            writer.WriteNumber("version", ExportTable.FormatVersion);
            writer.WriteString("title", table.Title);
            writer.WriteStartObject("properties");
            foreach (var property in table.Properties)
                writer.WriteString(property.Key, property.Value);
            writer.WriteEndObject();
            writer.WriteStartArray("columns");
            foreach (var column in table.Columns)
            {
                writer.WriteStartObject();
                writer.WriteString("key", column.Key);
                writer.WriteString(
                    "kind",
                    JsonNamingPolicy.CamelCase.ConvertName(column.Kind.ToString())
                );
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("rows");
            foreach (var row in table.Rows)
            {
                writer.WriteStartObject();
                for (int index = 0; index < row.Length; index++)
                {
                    var column = table.Columns[index];
                    writer.WritePropertyName(column.Key);
                    switch (column.Kind, row[index])
                    {
                        case (_, null):
                            writer.WriteNullValue();
                            break;
                        case (ExportValueKind.Text, var value):
                            writer.WriteStringValue((string)value);
                            break;
                        case (ExportValueKind.Boolean, var value):
                            writer.WriteBooleanValue((bool)value);
                            break;
                        case (_, var value):
                            // JSON numbers have no range limit; 128-bit sizes stay exact.
                            writer.WriteRawValue(
                                ExportTable
                                    .Integer(value)!
                                    .Value.ToString(CultureInfo.InvariantCulture)
                            );
                            break;
                    }
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteParquet(
        IParquetTableWriter parquet,
        string path,
        ExportTable table,
        CancellationToken token
    )
    {
        var columns = table
            .Columns.Select(column => new ParquetColumn(
                column.Key,
                column.Kind switch
                {
                    ExportValueKind.Text => ParquetColumnType.Text,
                    ExportValueKind.Boolean => ParquetColumnType.Boolean,
                    _ => ParquetColumnType.Integer,
                }
            ))
            .ToArray();
        var rows = table.Rows.Select(row =>
            row.Select(
                    (value, index) =>
                        table.Columns[index].Kind is ExportValueKind.Bytes or ExportValueKind.Count
                            ? ExportTable.Integer(value) is { } number
                                ? (object)checked((long)number)
                                : null
                            : value
                )
                .ToArray()
        );
        var metadata = table
            .Properties.Select(property => KeyValuePair.Create(property.Key, property.Value))
            .Prepend(KeyValuePair.Create("title", table.Title))
            .Prepend(
                KeyValuePair.Create(
                    "format",
                    $"{ExportTable.FormatName} {ExportTable.FormatVersion}"
                )
            );
        parquet.Write(path, columns, rows, metadata, token);
    }

    private static string Raw(object? value) =>
        value switch
        {
            null => "",
            string text => text,
            bool flag => flag ? "true" : "false",
            _ => ExportTable.Integer(value)!.Value.ToString(CultureInfo.InvariantCulture),
        };

    private static string SingleLine(string text) =>
        text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

    // East Asian wide and full-width characters take two columns in a monospaced view.
    internal static int DisplayWidth(string text)
    {
        int width = 0;
        foreach (var rune in text.EnumerateRunes())
            width += rune.Value
                is >= 0x1100
                    and <= 0x115F
                    or >= 0x2E80
                    and <= 0xA4CF
                    or >= 0xAC00
                    and <= 0xD7A3
                    or >= 0xF900
                    and <= 0xFAFF
                    or >= 0xFE30
                    and <= 0xFE4F
                    or >= 0xFF00
                    and <= 0xFF60
                    or >= 0xFFE0
                    and <= 0xFFE6
                    or >= 0x1F300
                    and <= 0x1FAFF
                    or >= 0x20000
                ? 2
                : 1;
        return width;
    }
}
