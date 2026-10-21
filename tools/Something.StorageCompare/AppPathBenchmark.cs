using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Something.Application;
using Something.Statistics;
using Something.Storage;

namespace Something.StorageCompare;

// Measures the application's own storage APIs (publish, open, browse, analysis, comparison)
// on synthetic history. Checksums must stay identical across storage optimizations.
internal static class AppPathBenchmark
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Timing(string Name, double FirstMilliseconds, double WarmMilliseconds);

    internal static int Run(string[] args)
    {
        try
        {
            return Measure(args);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static int Measure(string[] args)
    {
        int count = 100_000,
            generations = 6,
            budgetMiB = 1024,
            repeats = 3;
        string shape = "deep";
        string? output = null;
        bool memoryReport = false;
        string? database = null;
        bool profile = false;
        bool managedMemory = false;
        int? searchBudgetMiB = null;
        bool searchesOnly = false;
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length)
                throw new ArgumentException("Options require a value.");
            switch (args[i])
            {
                case "--count":
                    count = int.Parse(args[i + 1]);
                    break;
                case "--generations":
                    generations = int.Parse(args[i + 1]);
                    break;
                case "--budget-mib":
                    budgetMiB = int.Parse(args[i + 1]);
                    break;
                case "--repeats":
                    repeats = int.Parse(args[i + 1]);
                    break;
                case "--shape":
                    shape = args[i + 1];
                    break;
                case "--output":
                    output = args[i + 1];
                    break;
                case "--managed-memory":
                    managedMemory = bool.Parse(args[i + 1]);
                    break;
                case "--profile":
                    profile = bool.Parse(args[i + 1]);
                    break;
                case "--database":
                    database = args[i + 1];
                    break;
                case "--memory-report":
                    memoryReport = bool.Parse(args[i + 1]);
                    break;
                case "--search-budget-mib":
                    searchBudgetMiB = int.Parse(args[i + 1]);
                    break;
                case "--searches-only":
                    searchesOnly = bool.Parse(args[i + 1]);
                    break;
                default:
                    throw new ArgumentException($"Unknown option {args[i]}");
            }
        }
        if (
            count is < 100 or > 10_000_000
            || generations is < 2 or > 365
            || budgetMiB is < 1024 or > 65536
            || searchBudgetMiB is < 1024 or > 65536
            || repeats is < 1 or > 50
            || shape is not ("mixed" or "wide" or "deep")
        )
            throw new ArgumentException("Invalid application path benchmark options.");
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        var token = cancellation.Token;
        // --database keeps the history directory; when it already holds history, only queries run.
        string directory = Path.GetFullPath(
            database
                ?? Path.Combine("artifacts", "app-path-benchmark", Guid.NewGuid().ToString("N"))
        );
        bool reuse =
            database is not null
            && File.Exists(Path.Combine(directory, ScanHistoryArchive.DatabaseFileName));
        try
        {
            // A fixed clock keeps every generation inside the all-retained window.
            var archive = new ScanHistoryArchive(
                directory,
                new FixedClock(HistoryDedupData.End.AddDays(1)),
                TimeZoneInfo.Utc
            );
            var publishes = new List<object>();
            var ids = new List<Guid>();
            string volume = HistoryDedupData.Create(100, 0, shape, token).Descriptor.VolumeKey;
            if (reuse)
                ids.AddRange(archive.List(volume, token).Reverse().Select(entry => entry.Id));
            // Declared outside the loop and cleared first, so the previous generation's buffer is
            // unreachable before the next managed-memory baseline is taken.
            BufferedScan? buffer = null;
            for (int generation = 0; !reuse && generation < generations; generation++)
            {
                buffer = null;
                long heapBefore = managedMemory ? GC.GetTotalMemory(true) : 0;
                var building = managedMemory ? new HeapSampler() : null;
                buffer = HistoryDedupData.Create(count, generation, shape, token);
                long buildPeak = building?.Stop() ?? 0;
                long retained = managedMemory ? GC.GetTotalMemory(true) - heapBefore : 0;
                var publishing = managedMemory ? new HeapSampler() : null;
                volume = buffer.Descriptor.VolumeKey;
                var stages = new Dictionary<string, double>();
                string? stage = null;
                long stageStart = 0;
                void Close(long now)
                {
                    if (stage is not null)
                        stages[stage] =
                            stages.GetValueOrDefault(stage)
                            + Stopwatch.GetElapsedTime(stageStart, now).TotalSeconds;
                }
                using var self = Process.GetCurrentProcess();
                long writtenBefore = ProcessIo.WrittenBytes(self);
                var watch = Stopwatch.StartNew();
                var entry = archive.Publish(
                    buffer,
                    buffer.Descriptor.StartedUtc.AddSeconds(30),
                    "Synthetic",
                    TimeSpan.FromSeconds(30),
                    (ulong)budgetMiB << 20,
                    token,
                    progress =>
                    {
                        string name = progress.Stage.ToString();
                        if (name == stage)
                            return;
                        long now = Stopwatch.GetTimestamp();
                        Close(now);
                        stage = name;
                        stageStart = now;
                        if (memoryReport && File.Exists(archive.DatabasePath))
                            ReportMemory(archive.DatabasePath, generation, progress, token);
                    }
                );
                Close(Stopwatch.GetTimestamp());
                double seconds = watch.Elapsed.TotalSeconds;
                long publishPeak = publishing?.Stop() ?? 0;
                if (managedMemory)
                    Console.WriteLine(
                        $"  managed: generation={generation}; build-peak={(buildPeak - heapBefore) / 1048576.0:F0}MiB; retained={retained / 1048576.0:F0}MiB ({(double)retained / buffer.Count:F0} B/observation); publish-peak={(publishPeak - heapBefore) / 1048576.0:F0}MiB"
                    );
                long written = ProcessIo.WrittenBytes(self) - writtenBefore;
                ids.Add(entry.Id);
                long bytes = new FileInfo(archive.DatabasePath).Length;
                Console.WriteLine(
                    $"generation={generation}; publish={seconds:F3}s; file={bytes / 1048576.0:F1}MiB; written={written / 1048576.0:F1}MiB; "
                        + string.Join("; ", stages.Select(s => $"{s.Key}={s.Value:F3}s"))
                );
                publishes.Add(
                    new
                    {
                        Generation = generation,
                        PublishSeconds = seconds,
                        DatabaseBytes = bytes,
                        WrittenBytes = written,
                        Stages = stages,
                    }
                );
            }

            var timings = new List<Timing>();
            var checksums = new Dictionary<string, string>();
            T Time<T>(string name, Func<T> action, Func<T, string> checksum)
            {
                var watch = Stopwatch.StartNew();
                var result = action();
                double first = watch.Elapsed.TotalMilliseconds;
                var warm = new List<double>();
                for (int i = 1; i < repeats; i++)
                {
                    watch.Restart();
                    result = action();
                    warm.Add(watch.Elapsed.TotalMilliseconds);
                }
                warm.Sort();
                double median = warm.Count == 0 ? first : warm[warm.Count / 2];
                timings.Add(new(name, first, median));
                checksums[name] = checksum(result);
                Console.WriteLine($"  {name}: first={first:F1}ms; warm={median:F1}ms");
                return result;
            }

            if (searchesOnly)
            {
                MeasureSearches(archive, volume, searchBudgetMiB, Time<string>, token);
                return 0;
            }
            if (profile)
                QueryProfile.Run(archive.DatabasePath, ids[^1], token);
            Time("history-list", () => archive.List(volume, token), rows => rows.Length.ToString());
            Time(
                "open-latest",
                () =>
                {
                    using var opened = archive.Open(ids[^1], token);
                    return opened.Entry.Id;
                },
                _ => ""
            );
            // Keep exactly one reader open for the queries, like the application.
            using var snapshot = archive.Open(ids[^1], token);
            var store = snapshot.Store;
            var root = Time(
                "snapshot-summary",
                () => store.ReadSnapshotSummary(ids[^1], token).Root,
                r => r.Reference.ToString()
            );
            Time(
                "root-first-page",
                () =>
                    store.ReadFolderEntries(
                        ids[^1],
                        root.Reference,
                        includeTotalCount: true,
                        cancellationToken: token
                    ),
                page =>
                    Hash(page.Entries.Select(e => $"{e.Reference}|{e.Name}|{e.Data}"))
                    + page.TotalCount
            );
            if (profile)
            {
                Guid latest = ids[^1];
                Time(
                    "aggregation",
                    () => store.ReadAggregation(ids[^1], token).Folders.Count,
                    n => n.ToString()
                );
                Time(
                    "folder-names",
                    () =>
                        store
                            .HistoryContext.FolderNames.AsNoTracking()
                            .Where(n => n.ScanId == latest)
                            .AsEnumerable()
                            .Count(),
                    n => n.ToString()
                );
            }
            Time(
                "folder-analysis",
                () => store.ReadFolderAnalysis(ids[^1], new(), token),
                rows => Hash(rows.Select(r => $"{r.Sample.Reference}|{r.RelativePath}|{r.Data}"))
            );
            Time(
                "visibility-folders",
                () => store.ReadVisibilityFolders(ids[^1], token).ToArray(),
                rows => Hash(rows.Select(r => $"{r.Reference}|{r.StandardFileAttributes}"))
            );
            Time("read-files", () => store.ReadFiles(ids[^1], token).Count(), n => n.ToString());
            Time(
                "visibility-projection",
                () =>
                    new VisibleScan(
                        root.Reference,
                        () => store.ReadFiles(ids[^1], token),
                        new(false, false),
                        token,
                        store.ReadVisibilityFolders(ids[^1], token)
                    ),
                scan => scan.Aggregation.Folders[root.Reference].Entries?.ToString() ?? ""
            );
            Time(
                "folder-changes",
                () => archive.ReadFolderChanges(volume, ids[0], ids[^1], token),
                report =>
                    Hash(
                        report.Folders.Select(f =>
                            $"{f.Reference}|{f.BeforePath}|{f.AfterPath}|{f.AllocatedDelta}|{f.Status}"
                        )
                    )
            );
            MeasureSearches(archive, volume, searchBudgetMiB, Time<string>, token);

            string json = JsonSerializer.Serialize(
                new
                {
                    GeneratedUtc = DateTimeOffset.UtcNow,
                    Count = count,
                    Shape = shape,
                    Generations = generations,
                    BudgetMiB = budgetMiB,
                    SearchBudgetMiB = searchBudgetMiB,
                    Repeats = repeats,
                    Publishes = publishes,
                    Queries = timings,
                    Checksums = checksums,
                    PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                },
                new JsonSerializerOptions { WriteIndented = true }
            );
            if (output is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                File.WriteAllText(output, json);
            }
            Console.WriteLine(
                $"generations={publishes.Count}; peak={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F0}MiB; checksums: "
                    + string.Join("; ", checksums.Select(c => $"{c.Key}={c.Value}"))
            );
            return 0;
        }
        finally
        {
            if (database is null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private delegate T Timed<T>(string name, Func<T> action, Func<T, string> checksum);

    // Searches every generation. Each criterion stresses one stage: an unfiltered result holds
    // every item, names are matched in .NET, changes and trends read the whole range.
    // By default searches run like the application: the engine starts with the memory available
    // now and grows through the budget. A fixed budget checks success on a low-memory machine;
    // it is not a limit of the application.
    private static void MeasureSearches(
        ScanHistoryArchive archive,
        string volume,
        int? fixedBudgetMiB,
        Timed<string> time,
        CancellationToken token
    )
    {
        var service = new HistorySearchService(archive, new(ScanMemoryBudget.ReadSystem));
        if (fixedBudgetMiB is null)
        {
            var budget = new ScanMemoryBudget(ScanMemoryBudget.ReadSystem);
            using var reservation = budget.ReserveAsync(1, token).GetAwaiter().GetResult();
            Console.WriteLine(
                $"  search budget from available memory: {reservation.SizeDuckDatabase() / 1048576.0:F0}MiB"
            );
        }
        else
            Console.WriteLine($"  search budget fixed: {fixedBudgetMiB}MiB (low-memory check)");
        HistorySearchResult Search(HistorySearchCriteria criteria) =>
            fixedBudgetMiB is { } mib
                ? archive.Search(volume, criteria, token, (ulong)mib << 20)
                : service.SearchAsync(volume, criteria, token).GetAwaiter().GetResult();
        var range = new HistorySearchCriteria(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        (string Name, HistorySearchCriteria Criteria)[] searches =
        [
            ("search-all", range),
            (
                "search-files-size",
                range with
                {
                    Target = SearchTarget.Files,
                    MinimumSize = 300 << 10,
                }
            ),
            (
                "search-name-regex",
                range with
                {
                    NamePattern = @"^synthetic-\d*77$",
                    NameMode = NameMatchMode.RegularExpression,
                }
            ),
            ("search-delta", range with { MinimumDelta = 1 }),
            ("search-trend", range with { Trend = SizeTrend.Increasing }),
            (
                "search-streams-size",
                range with
                {
                    Attributes = new(NamedStreams: AttributeRequirement.Set),
                    MinimumSize = 300 << 10,
                }
            ),
        ];
        foreach (var (name, criteria) in searches)
            time(
                name,
                () =>
                {
                    long heap = GC.GetTotalMemory(true);
                    using var result = Search(criteria);
                    long retained = GC.GetTotalMemory(true) - heap;
                    Console.WriteLine(
                        $"    {name}: scans={result.Scans.Length}; folders={result.MatchedFolders}; files={result.MatchedFiles}; uncertain={result.UncertainCount}; unknown-attributes={result.UnknownAttributeCount}; retained={retained / 1048576.0:F0}MiB"
                    );
                    return $"{result.MatchedFolders}|{result.MatchedFiles}|{result.UncertainCount}|{result.UnknownAttributeCount}";
                },
                checksum => checksum
            );
        using var sample = Search(range with { Target = SearchTarget.Files, MinimumDelta = 1 });
        if (FirstFile(sample) is { } item)
            time(
                "search-series",
                () =>
                    Hash(
                        archive
                            .ReadSearchSeries(sample, item, token)
                            .Select(o => $"{o.ScanIndex}|{o.State}|{o.AllocatedBytes}")
                    ),
                checksum => checksum
            );

        static SearchItem? FirstFile(HistorySearchResult result)
        {
            var pending = new Stack<Something.Ntfs.FileReference>([result.Root]);
            while (pending.TryPop(out var folder))
                foreach (var entry in result.Entries(folder))
                {
                    if (!entry.IsDirectory)
                        return entry;
                    pending.Push(entry.Reference);
                }
            return null;
        }
    }

    // Samples the managed heap every 5 ms; reports the maximum, including garbage not yet collected.
    private sealed class HeapSampler
    {
        private long peak = GC.GetTotalMemory(false);
        private readonly Timer timer;

        internal HeapSampler() =>
            timer = new(
                _ => InterlockedMax(GC.GetTotalMemory(false)),
                null,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(5)
            );

        private void InterlockedMax(long value)
        {
            long current;
            while (value > (current = Volatile.Read(ref peak)))
                if (Interlocked.CompareExchange(ref peak, value, current) == current)
                    return;
        }

        internal long Stop()
        {
            timer.Dispose();
            InterlockedMax(GC.GetTotalMemory(false));
            return Volatile.Read(ref peak);
        }
    }

    // Shares the publishing engine (same process and file) to show what holds memory before commit.
    private static void ReportMemory(
        string path,
        int generation,
        ScanWorkProgress progress,
        CancellationToken token
    )
    {
        using var store = ScanStore.OpenSharedHistoryWriter(path, null, token);
        using var command = store.HistoryContext.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT tag,memory_usage_bytes,temporary_storage_bytes FROM duckdb_memory() WHERE memory_usage_bytes>0 OR temporary_storage_bytes>0 ORDER BY memory_usage_bytes DESC";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(
                $"{reader.GetString(0)}={reader.GetInt64(1) / 1048576.0:F0}MiB(+{reader.GetInt64(2) / 1048576.0:F0}MiB disk)"
            );
        Console.WriteLine(
            $"  memory at {progress.Stage}: generation={generation}; attempt={progress.RetryAttempt}; spilling={progress.SpillingToDisk}; "
                + string.Join("; ", rows)
        );
    }

    private static string Hash(IEnumerable<string> rows)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string row in rows)
            hash.AppendData(Encoding.UTF8.GetBytes(row + "\n"));
        return Convert.ToHexString(hash.GetHashAndReset())[..16];
    }
}
