using System.Numerics;
using System.Text;
using System.Text.Json;
using DuckDB.NET.Data;
using Something.Application.Export;

namespace Something.Ntfs.Tests;

public sealed class TableExportTests
{
    private static readonly ExportColumn[] Columns =
    [
        new("path", "パス", ExportValueKind.Text),
        new("sizeBytes", "サイズ", ExportValueKind.Bytes),
        new("deltaBytes", "増減", ExportValueKind.Bytes),
        new("fileCount", "ファイル数", ExportValueKind.Count),
        new("hidden", "隠し", ExportValueKind.Boolean),
    ];

    private static ExportTable Table(params object?[][] rows) =>
        new("容量の一覧", [new("drive", "ドライブ", "C:")], Columns, rows);

    private static string Display(ExportColumn column, object? value) =>
        value switch
        {
            null => "不明",
            bool flag => flag ? "はい" : "",
            string text => text,
            _ => $"{ExportTable.Integer(value)} B",
        };

    [Fact]
    public void Csv_QuotesSeparatorsQuotesAndLineBreaks_AndKeepsRawValues()
    {
        // Arrange
        var table = Table(
            ["a,b", (UInt128)10, new BigInteger(-3), 2UL, true],
            ["say \"hi\"\nnext", null, null, null, false]
        );

        // Act
        string csv = TableExporter.Delimited(table, ',', TableExporter.Csv);

        // Assert
        Assert.Equal(
            "path,sizeBytes,deltaBytes,fileCount,hidden\r\n"
                + "\"a,b\",10,-3,2,true\r\n"
                + "\"say \"\"hi\"\"\nnext\",,,,false\r\n",
            csv
        );
    }

    [Fact]
    public async Task Tsv_ReplacesTabsAndLineBreaks_AndWritesUtf8WithByteOrderMark()
    {
        // Arrange
        string path = TemporaryPath(".tsv");
        var table = Table(["日本語\tname\r\nx", (UInt128)1, null, 0UL, false]);

        try
        {
            // Act
            await TableExporter.WriteAsync(
                path,
                ExportFormat.Tsv,
                table,
                Display,
                TestContext.Current.CancellationToken
            );

            // Assert
            byte[] bytes = await File.ReadAllBytesAsync(
                path,
                TestContext.Current.CancellationToken
            );
            Assert.Equal(Encoding.UTF8.Preamble.ToArray(), bytes[..3]);
            Assert.Equal(
                "path\tsizeBytes\tdeltaBytes\tfileCount\thidden\r\n日本語 name  x\t1\t\t0\tfalse\r\n",
                Encoding.UTF8.GetString(bytes[3..])
            );
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Json_WritesMetadataAndExact128BitNumbers()
    {
        // Arrange
        var huge = UInt128.MaxValue;
        var table = Table(["x", huge, new BigInteger(-5), 7L, null]);

        // Act
        using var document = JsonDocument.Parse(TableExporter.Json(table));

        // Assert
        var root = document.RootElement;
        Assert.Equal(ExportTable.FormatName, root.GetProperty("format").GetString());
        Assert.Equal(ExportTable.FormatVersion, root.GetProperty("version").GetInt32());
        Assert.Equal("C:", root.GetProperty("properties").GetProperty("drive").GetString());
        Assert.Equal("bytes", root.GetProperty("columns")[1].GetProperty("kind").GetString());
        var row = root.GetProperty("rows")[0];
        Assert.Equal(huge.ToString(), row.GetProperty("sizeBytes").GetRawText());
        Assert.Equal(-5, row.GetProperty("deltaBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("hidden").ValueKind);
    }

    [Fact]
    public void Properties_UseRawValuesForToolsAndDisplayValuesForPeople()
    {
        // Arrange
        var table = new ExportTable(
            "一覧",
            [new("limit", "件数の上限", "", "制限なし")],
            Columns,
            []
        );

        // Act
        using var json = JsonDocument.Parse(TableExporter.Json(table));
        string text = TableExporter.PlainText(table, Display);

        // Assert
        Assert.Equal(
            "",
            json.RootElement.GetProperty("properties").GetProperty("limit").GetString()
        );
        Assert.Contains("件数の上限: 制限なし", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_EncodesTextAndUsesDisplayFormatting()
    {
        // Arrange
        var table = Table(["<script>&", (UInt128)5, null, 1UL, true]);

        // Act
        string html = TableExporter.Html(table, Display);

        // Assert
        Assert.Contains("<td>&lt;script&gt;&amp;</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"number\">5 B</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"number\">不明</td>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>ドライブ</dt><dd>C:</dd>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainText_AlignsWideCharactersAndRightAlignsNumbers()
    {
        // Arrange
        var table = Table(
            ["フォルダー", (UInt128)5, null, 1UL, false],
            ["ab", (UInt128)12345, null, 2UL, true]
        );

        // Act
        string[] lines = TableExporter.PlainText(table, Display).Split("\r\n");

        // Assert
        Assert.Equal("容量の一覧", lines[0]);
        Assert.Equal("ドライブ: C:", lines[1]);
        string header = lines[3];
        Assert.StartsWith("パス", header, StringComparison.Ordinal);
        // Right-aligned sizes end in the same display column despite the wide folder name.
        Assert.Equal(End(lines[5], " 5 B"), End(lines[6], "12345 B"));
        Assert.Equal(10, TableExporter.DisplayWidth("フォルダー"));
    }

    [Fact]
    public async Task Parquet_RoundTripsColumnsValuesAndMetadata()
    {
        // Arrange
        string path = TemporaryPath(".parquet");
        var table = Table(
            ["日本語'path", (UInt128)42, new BigInteger(-7), 3UL, true],
            ["b", null, null, null, null]
        );

        try
        {
            // Act
            await TableExporter.WriteAsync(
                path,
                ExportFormat.Parquet,
                table,
                Display,
                TestContext.Current.CancellationToken
            );

            // Assert
            using var connection = new DuckDBConnection("Data Source=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            string literal = "'" + path.Replace("'", "''") + "'";
            command.CommandText =
                $"SELECT path, sizeBytes, deltaBytes, fileCount, hidden FROM read_parquet({literal}) ORDER BY path";
            using (var reader = command.ExecuteReader())
            {
                Assert.True(reader.Read());
                Assert.Equal("b", reader.GetString(0));
                Assert.True(reader.IsDBNull(1));
                Assert.True(reader.Read());
                Assert.Equal("日本語'path", reader.GetString(0));
                Assert.Equal(42L, reader.GetInt64(1));
                Assert.Equal(-7L, reader.GetInt64(2));
                Assert.Equal(3L, reader.GetInt64(3));
                Assert.True(reader.GetBoolean(4));
                Assert.False(reader.Read());
            }
            command.CommandText =
                $"SELECT decode(value) FROM parquet_kv_metadata({literal}) WHERE decode(key) = 'drive'";
            Assert.Equal("C:", command.ExecuteScalar());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Parquet_RejectsSizesBeyond64Bits_AndLeavesNoFile()
    {
        // Arrange
        string path = TemporaryPath(".parquet");
        var table = Table(["x", (UInt128)ulong.MaxValue, null, null, null]);

        // Act
        await Assert.ThrowsAsync<OverflowException>(() =>
            TableExporter.WriteAsync(
                path,
                ExportFormat.Parquet,
                table,
                Display,
                TestContext.Current.CancellationToken
            )
        );

        // Assert
        Assert.False(File.Exists(path));
        Assert.Empty(
            Directory.GetFiles(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}*")
        );
    }

    [Theory]
    [InlineData(ExportFormat.PlainText)]
    [InlineData(ExportFormat.Html)]
    [InlineData(ExportFormat.Csv)]
    [InlineData(ExportFormat.Tsv)]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.Parquet)]
    public async Task EveryFormat_WritesAnEmptyTableWithItsColumns(ExportFormat format)
    {
        // Arrange
        string path = TemporaryPath(ExportTable.Extension(format));

        try
        {
            // Act
            await TableExporter.WriteAsync(
                path,
                format,
                Table(),
                Display,
                TestContext.Current.CancellationToken
            );

            // Assert
            byte[] bytes = await File.ReadAllBytesAsync(
                path,
                TestContext.Current.CancellationToken
            );
            string text = Encoding.UTF8.GetString(bytes);
            switch (format)
            {
                case ExportFormat.Parquet:
                    Assert.Equal("PAR1"u8.ToArray(), bytes[..4]);
                    break;
                case ExportFormat.Json:
                    Assert.Equal(
                        0,
                        JsonDocument.Parse(bytes).RootElement.GetProperty("rows").GetArrayLength()
                    );
                    break;
                case ExportFormat.PlainText or ExportFormat.Html:
                    Assert.Contains("ファイル数", text, StringComparison.Ordinal);
                    break;
                default:
                    Assert.Contains("fileCount", text, StringComparison.Ordinal);
                    break;
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("abc", 3)]
    [InlineData("ｱｲｳ", 3)]
    [InlineData("ＡＢ", 4)]
    [InlineData("日本😀", 6)]
    [InlineData("", 0)]
    public void DisplayWidth_CountsWideCharactersAsTwoColumns(string text, int width) =>
        Assert.Equal(width, TableExporter.DisplayWidth(text));

    [Fact]
    public void Html_EncodesTitlesAndPropertyValues()
    {
        // Arrange
        var table = new ExportTable(
            "<b>&\"",
            [new("scope", "起点<", "C:\a&b")],
            [new("path", "パス<", ExportValueKind.Text)],
            []
        );

        // Act
        string html = TableExporter.Html(table, Display);

        // Assert
        Assert.Contains("<title>&lt;b&gt;&amp;&quot;</title>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>起点&lt;</dt><dd>C:\a&amp;b</dd>", html, StringComparison.Ordinal);
        Assert.Contains("<th>パス&lt;</th>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_RejectsRowsWithWrongWidth_AndLeavesNoFile()
    {
        // Arrange
        string path = TemporaryPath(".csv");
        var table = Table(["only one value"]);

        // Act
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            TableExporter.WriteAsync(
                path,
                ExportFormat.Csv,
                table,
                Display,
                TestContext.Current.CancellationToken
            )
        );

        // Assert
        Assert.Equal("table", error.ParamName);
        Assert.False(File.Exists(path));
        Assert.Empty(
            Directory.GetFiles(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}*")
        );
    }

    [Fact]
    public async Task Write_CancelledBeforeMove_KeepsExistingFile()
    {
        // Arrange
        string path = TemporaryPath(".json");
        await File.WriteAllTextAsync(path, "previous", TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        try
        {
            // Act
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                TableExporter.WriteAsync(
                    path,
                    ExportFormat.Json,
                    Table(["x", null, null, null, null]),
                    Display,
                    cancelled.Token
                )
            );

            // Assert
            Assert.Equal(
                "previous",
                await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)
            );
            Assert.Empty(
                Directory.GetFiles(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}*")
            );
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static int End(string line, string cell) =>
        TableExporter.DisplayWidth(
            line[..(line.IndexOf(cell, StringComparison.Ordinal) + cell.Length)]
        );

    private static string TemporaryPath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"something-export-{Guid.NewGuid():N}{extension}");
}
