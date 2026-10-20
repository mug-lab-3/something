using System.Globalization;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Something.Application;
using Something.Application.Export;
using Something.Storage;

namespace Something.App;

public sealed partial class MainWindow
{
    private ExportDialog? exportDialog;

    private async void OnExport(object sender, RoutedEventArgs args)
    {
        if (closed || exportDialog is not null)
            return;
        if (session is not { } current)
        {
            ShowToast(Strings.Get("Export_NoScan"), InfoBarSeverity.Informational);
            return;
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
        exportDialog = dialog;
        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        finally
        {
            exportDialog = null;
        }
        if (result != ContentDialogResult.Primary || closed)
            return;
        var options = dialog.Options;
        string? path = await PickExportPathAsync(current, options.Format);
        if (path is null || closed)
            return;
        var task = ExportAsync(current, options, path);
        historyTasks.Add(task);
        try
        {
            await task;
            if (!closed)
                ShowToast(
                    path,
                    InfoBarSeverity.Success,
                    Strings.Get("Export_Completed"),
                    replacePersistent: true
                );
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!closed)
                ShowToast(
                    StatusText.Describe(error),
                    InfoBarSeverity.Error,
                    Strings.Get("Export_FailedTitle")
                );
        }
        finally
        {
            historyTasks.Remove(task);
        }
    }

    // The list tabs can be exported as shown; the history tab has no folder list.
    private string? DisplayedListDescription(ScanSession current)
    {
        if (ReferenceEquals(WorkspaceTabs.SelectedItem, DatabaseWorkspaceTab))
            return null;
        if (Analyzing)
            return Ranking.ItemsSource is AnalysisRowCollection rows
                ? Strings.Format("Export_DisplayedAnalysis", rows.TotalCount)
                : null;
        return Entries.ItemsSource is FolderEntryCollection
            ? Strings.Format("Export_DisplayedFiles", location?.Path ?? current.Drive + "\\")
            : null;
    }

    private async Task<string?> PickExportPathAsync(ScanSession current, ExportFormat format)
    {
        string extension = ExportTable.Extension(format);
        var picker = new FileSavePicker(AppWindow.Id)
        {
            SuggestedFileName =
                $"Something-{current.Drive.TrimEnd(':')}-{DateTime.Now:yyyyMMdd-HHmmss}",
            DefaultFileExtension = extension,
        };
        picker.FileTypeChoices.Add(
            format switch
            {
                ExportFormat.PlainText => Strings.Get("Export_FileTypeText"),
                ExportFormat.Html => Strings.Get("Export_FileTypeHtml"),
                ExportFormat.Csv => Strings.Get("Export_FileTypeCsv"),
                ExportFormat.Tsv => Strings.Get("Export_FileTypeTsv"),
                ExportFormat.Json => Strings.Get("Export_FileTypeJson"),
                _ => Strings.Get("Export_FileTypeParquet"),
            },
            [extension]
        );
        return (await picker.PickSaveFileAsync())?.Path;
    }

    private async Task ExportAsync(ScanSession current, ExportOptions options, string path)
    {
        await using var update = BeginViewUpdate(
            Path.GetFileName(path),
            LoadingAnimationKind.Export
        );
        var table =
            options.Scope == ExportScope.Displayed
                ? await DisplayedTableAsync(current)
                : await FolderSummaryTableAsync(current, options);
        await TableExporter.WriteAsync(path, options.Format, table, ExportDisplay, lifetime.Token);
    }

    private async Task<ExportTable> DisplayedTableAsync(ScanSession current)
    {
        bool comparing = Comparing;
        IEnumerable<ExportItem> items;
        if (Analyzing && Ranking.ItemsSource is AnalysisRowCollection analysis)
            items = analysis
                .AllRows()
                .Select(row => ExportItem.From(row.Item, current.Drive, row.StandardFileAttributes))
                .ToArray();
        else if (Entries.ItemsSource is FolderEntryCollection entries)
        {
            string folder = (location?.Path ?? current.Drive + "\\").TrimEnd('\\');
            items = (await entries.ReadAllAsync(lifetime.Token))
                .Select(row => ExportItem.From(row, folder))
                .ToArray();
        }
        else
            throw new InvalidOperationException(Strings.Get("Export_DisplayedUnavailable"));
        return BuildTable(
            current,
            Analyzing ? Strings.Get("Export_TitleAnalysis") : Strings.Get("Export_TitleFiles"),
            [],
            items,
            comparing,
            includeDepth: false
        );
    }

    private async Task<ExportTable> FolderSummaryTableAsync(
        ScanSession current,
        ExportOptions options
    )
    {
        bool comparing = Comparing;
        var report = comparing ? comparisonReport : null;
        var start = location ?? new(current.Root, current.Drive + "\\");
        string relativeStart =
            start.Reference == current.Root
                ? ""
                : start.Path[(current.Drive.Length + 1)..].TrimEnd('\\');
        var folders = await current.ReadFolderAnalysisAsync(new(), lifetime.Token);
        var attributes = await current.ReadFolderAttributesAsync(lifetime.Token);
        var changes = report
            ?.Folders.Where(row => row.AfterPath is not null)
            .ToDictionary(row => row.Reference);
        var limits = new FolderSummaryLimits(
            options.MaximumDepth,
            options.MinimumBytes,
            options.Limit,
            options.SortByChange
        );
        var items = await Task.Run(
            () =>
                FolderSummarySelection
                    .Select(
                        // The folder analysis excludes the root, which summarizes the whole drive.
                        folders
                            .Select(folder =>
                                ExportItem.FromFolder(
                                    current.Drive,
                                    folder.RelativePath,
                                    folder.Sample,
                                    attributes.GetValueOrDefault(folder.Sample.Reference),
                                    changes?.GetValueOrDefault(folder.Sample.Reference),
                                    comparing
                                )
                            )
                            .Prepend(
                                ExportItem.FromFolder(
                                    current.Drive,
                                    "",
                                    current.RootSample,
                                    attributes.GetValueOrDefault(current.Root),
                                    changes?.GetValueOrDefault(current.Root),
                                    comparing
                                )
                            ),
                        relativeStart,
                        limits,
                        item => item.RelativePath,
                        item => item.SizeBytes,
                        item => item.DeltaBytes
                    )
                    .Select(row => row.Folder with { Depth = row.Depth })
                    .ToArray(),
            lifetime.Token
        );
        var filters = new List<ExportProperty>
        {
            new("scope", Strings.Get("Export_PropertyScope"), start.Path),
            new(
                "maximumDepth",
                Strings.Get("Export_PropertyMaximumDepth"),
                options.MaximumDepth?.ToString(CultureInfo.InvariantCulture) ?? "",
                options.MaximumDepth is null ? Strings.Get("Export_NoLimit") : null
            ),
            new(
                "minimumBytes",
                Strings.Get("Export_PropertyMinimumSize"),
                options.MinimumBytes.ToString(CultureInfo.InvariantCulture)
            ),
            new(
                "limit",
                Strings.Get("Export_PropertyLimit"),
                options.Limit?.ToString(CultureInfo.InvariantCulture) ?? "",
                options.Limit is null ? Strings.Get("Export_NoLimit") : null
            ),
        };
        return BuildTable(
            current,
            Strings.Get("Export_TitleSummary"),
            filters,
            items,
            comparing,
            includeDepth: true
        );
    }

    private ExportTable BuildTable(
        ScanSession current,
        string title,
        IEnumerable<ExportProperty> filters,
        IEnumerable<ExportItem> items,
        bool comparing,
        bool includeDepth
    )
    {
        var columns = new List<ExportColumn>
        {
            new("path", Strings.Get("Export_ColumnPath"), ExportValueKind.Text),
            new("kind", Strings.Get("Export_ColumnKind"), ExportValueKind.Text),
        };
        if (includeDepth)
            columns.Add(new("depth", Strings.Get("Export_ColumnDepth"), ExportValueKind.Count));
        columns.Add(new("sizeBytes", Strings.Get("Export_ColumnSize"), ExportValueKind.Bytes));
        if (includeDepth)
            columns.Add(
                new("fileCount", Strings.Get("Export_ColumnFileCount"), ExportValueKind.Count)
            );
        if (comparing)
        {
            columns.Add(
                new("beforeBytes", Strings.Get("Export_ColumnBefore"), ExportValueKind.Bytes)
            );
            columns.Add(
                new("deltaBytes", Strings.Get("Export_ColumnDelta"), ExportValueKind.Bytes)
            );
            columns.Add(new("status", Strings.Get("Export_ColumnStatus"), ExportValueKind.Text));
        }
        columns.Add(new("hidden", Strings.Get("Export_ColumnHidden"), ExportValueKind.Boolean));
        columns.Add(new("system", Strings.Get("Export_ColumnSystem"), ExportValueKind.Boolean));
        columns.Add(
            new("incomplete", Strings.Get("Export_ColumnIncomplete"), ExportValueKind.Boolean)
        );

        var rows = items
            .Select(item =>
            {
                var values = new List<object?> { item.Path, item.IsDirectory ? "folder" : "file" };
                if (includeDepth)
                    values.Add(item.Depth);
                values.Add(item.SizeBytes);
                if (includeDepth)
                    values.Add(item.FileCount);
                if (comparing)
                {
                    values.Add(item.BeforeBytes);
                    values.Add(item.DeltaBytes);
                    values.Add(item.Status is { } status ? StatusCode(status) : null);
                }
                values.Add(Flag(item.Attributes, FileAttributes.Hidden));
                values.Add(Flag(item.Attributes, FileAttributes.System));
                values.Add(item.Incomplete);
                return values.ToArray();
            })
            .ToArray();

        var properties = new List<ExportProperty>
        {
            new("generator", Strings.Get("Export_PropertyGenerator"), AppVersion.WindowTitle),
            new("drive", Strings.Get("Export_PropertyDrive"), current.Drive),
            new(
                "scanCompleted",
                Strings.Get("Export_PropertyScanCompleted"),
                current.CompletedLocal?.ToString(
                    "yyyy-MM-dd HH:mm:ss zzz",
                    CultureInfo.InvariantCulture
                ) ?? ""
            ),
            new(
                "exportedAt",
                Strings.Get("Export_PropertyExportedAt"),
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
            ),
            new(
                "includeSystem",
                Strings.Get("Export_PropertyIncludeSystem"),
                visibilityOptions.IncludeSystem ? "true" : "false",
                YesNo(visibilityOptions.IncludeSystem)
            ),
            new(
                "includeHidden",
                Strings.Get("Export_PropertyIncludeHidden"),
                visibilityOptions.IncludeHidden ? "true" : "false",
                YesNo(visibilityOptions.IncludeHidden)
            ),
        };
        if (comparing && comparisonReport?.Baseline is { } baseline)
            properties.Add(
                new(
                    "comparisonBase",
                    Strings.Get("Export_PropertyComparisonBase"),
                    baseline
                        .CompletedUtc.ToLocalTime()
                        .ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
                )
            );
        properties.AddRange(filters);
        return new(title, properties, columns, rows, AppLanguage.Current);

        static string YesNo(bool value) =>
            value ? Strings.Get("Export_Yes") : Strings.Get("Export_No");

        static bool? Flag(uint? attributes, FileAttributes flag) =>
            attributes is { } value ? (value & (uint)flag) != 0 : null;
    }

    private static string StatusCode(FolderChangeStatus status) =>
        status switch
        {
            FolderChangeStatus.Comparable => "comparable",
            FolderChangeStatus.UncertainCapacity => "uncertain",
            FolderChangeStatus.NotObservedBefore => "added",
            FolderChangeStatus.NotObservedAfter => "removed",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    // Plain text and HTML show values the way the lists do.
    private static string ExportDisplay(ExportColumn column, object? value) =>
        (column.Key, value) switch
        {
            (_, null) => "—",
            ("kind", "folder") => Strings.Get("Item_Folder"),
            ("kind", _) => Strings.Get("Item_File"),
            ("status", "comparable") => Strings.Get("Export_StatusComparable"),
            ("status", "uncertain") => Strings.Get("Change_Uncomparable"),
            ("status", "added") => Strings.Get("Change_MissingBefore"),
            ("status", "removed") => Strings.Get("Change_MissingAfter"),
            ("deltaBytes", BigInteger delta) => DisplayText.Change(delta, null),
            (_, UInt128 bytes) => DisplayText.Bytes(bytes),
            (_, bool flag) => flag ? Strings.Get("Export_Yes") : "",
            (_, ulong count) => count.ToString("N0", CultureInfo.CurrentCulture),
            (_, int count) => count.ToString("N0", CultureInfo.CurrentCulture),
            (_, var other) => other.ToString() ?? "",
        };

    private sealed record ExportItem(
        string Path,
        bool IsDirectory,
        UInt128? SizeBytes,
        UInt128? BeforeBytes,
        BigInteger? DeltaBytes,
        FolderChangeStatus? Status,
        uint? Attributes,
        bool Incomplete
    )
    {
        public int? Depth { get; init; }
        public ulong? FileCount { get; init; }
        public string RelativePath { get; init; } = "";

        public static ExportItem From(CapacityAnalysisItem item, string drive, uint? attributes) =>
            new(
                drive + "\\" + item.RelativePath,
                item.IsDirectory,
                item.Comparing && !item.HasAfter ? null : item.AllocatedBytes,
                item.Comparing && item.HasBefore ? item.BeforeBytes : null,
                item.Delta,
                item.Comparing ? item.Status : null,
                attributes,
                item.CapacityIncomplete
            );

        public static ExportItem From(EntryRow row, string folder) =>
            new(
                folder + "\\" + row.Entry.Name,
                row.Entry.IsDirectory,
                row.Comparing && row.Change is { After: null }
                    ? null
                    : row.Entry.Data?.AllocatedBytes,
                row.Comparing ? row.Change?.BeforeBytes : null,
                row.Comparing ? row.Change?.Delta : null,
                row.Comparing ? row.Change?.Status : null,
                row.Entry.StandardFileAttributes,
                row.Entry.CapacityIncomplete || !row.Entry.NamesComplete
            );

        public static ExportItem FromFolder(
            string drive,
            string relativePath,
            Statistics.FolderCapacitySample sample,
            uint? attributes,
            FolderChange? change,
            bool comparing
        )
        {
            var data = sample.Entries?.SubtreeData ?? sample.SubtreeData;
            return new(
                drive + "\\" + relativePath,
                true,
                data.AllocatedBytes,
                comparing ? change?.BeforeAllocatedBytes : null,
                comparing ? change?.AllocatedDelta : null,
                comparing ? change?.Status : null,
                attributes,
                sample.SubtreeUnresolvedCount != 0
            )
            {
                FileCount = sample.Entries?.SubtreeFileCount ?? sample.SubtreeFileCount,
                RelativePath = relativePath,
            };
        }
    }
}
