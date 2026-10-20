using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Something.Application.Export;

namespace Something.App;

public sealed partial class MainWindow
{
    // The save picker cannot be driven in a smoke test; tables are written to the capture folder.
    private async Task VerifyExportAsync(string capture)
    {
        if (!demo || session is not { } current)
            throw new InvalidOperationException("Export smoke needs a demo scan.");
        WorkspaceTabs.SelectedIndex = 0;
        await Task.Delay(200, lifetime.Token);
        if (Entries.ItemsSource is not FolderEntryCollection entries)
            throw new InvalidOperationException("Export smoke needs the file list.");
        int shown = (await entries.ReadAllAsync(lifetime.Token)).Count;
        var displayed = await DisplayedTableAsync(current);
        var summaryOptions = new ExportOptions(
            ExportScope.FolderSummary,
            ExportFormat.Csv,
            1,
            UInt128.Zero,
            null,
            false
        );
        var summary = await FolderSummaryTableAsync(current, summaryOptions);
        int[] depths = [.. summary.Rows.Select(row => (int)row[2]!)];
        if (
            displayed.Rows.Count != shown
            || summary.Rows.Count < 2
            || depths.Count(depth => depth == 0) != 1
            || depths.Any(depth => depth > 1)
        )
            throw new InvalidOperationException(
                $"Export tables did not match the view: displayed={displayed.Rows.Count}/{shown}, summary depths={string.Join(",", depths)}."
            );
        foreach (var format in Enum.GetValues<ExportFormat>())
        {
            string path = capture + ".export" + ExportTable.Extension(format);
            // Run the same export path as the dialog, including its progress animation.
            await ExportAsync(current, summaryOptions with { Format = format }, path);
            var bytes = await File.ReadAllBytesAsync(path, lifetime.Token);
            bool valid = format switch
            {
                ExportFormat.Json => JsonDocument
                    .Parse(bytes)
                    .RootElement.GetProperty("rows")
                    .GetArrayLength() == summary.Rows.Count,
                ExportFormat.Parquet => bytes.Length > 8
                    && bytes[..4].SequenceEqual("PAR1"u8.ToArray()),
                _ => bytes.Length > 0,
            };
            if (!valid)
                throw new InvalidOperationException($"Export did not write a valid {format} file.");
        }
        var dialog = new ExportDialog(
            DisplayedListDescription(current),
            location?.Path ?? current.Drive + "\\",
            Comparing
        )
        {
            XamlRoot = RootLayout.XamlRoot,
            RequestedTheme = RootLayout.ActualTheme,
        };
        var showing = dialog.ShowAsync();
        try
        {
            await Task.Delay(300, lifetime.Token);
            if (dialog.Options is not { Scope: ExportScope.Displayed, Format: ExportFormat.Csv })
                throw new InvalidOperationException(
                    "Export dialog did not default to the shown list as CSV."
                );
            ((RadioButtons)dialog.FindName("ScopeChoice")).SelectedIndex = 1;
            await Task.Delay(200, lifetime.Token);
            await CaptureStartupFrameAsync(capture + ".export-dialog");
        }
        finally
        {
            dialog.Hide();
            await showing;
        }
        await File.WriteAllTextAsync(
            capture + ".export.result",
            $"Displayed list ({displayed.Rows.Count} rows), folder summary to depth 1 ({summary.Rows.Count} rows), all six formats and the dialog passed. Synthetic data only.",
            lifetime.Token
        );
    }
}
