using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Something.Storage;
using Something.StorageCompare;

if (args.FirstOrDefault() == "--history-dedup")
    return await HistoryDedupBenchmark.RunAsync(args[1..]);
if (args.FirstOrDefault() == "--visibility")
    return VisibilityBenchmark.Run(args[1..]);

int count = 100_000,
    repeats = 3,
    budgetMiB = 256;
string engine = "all";
string? output = null;
int? memoryBudgetMiB = null;
Guid runId = Guid.NewGuid();
try
{
    if (args.Length % 2 != 0)
        throw new ArgumentException("Options require a value.");
    for (int i = 0; i < args.Length; i += 2)
        switch (args[i])
        {
            case "--count":
                count = int.Parse(args[i + 1]);
                break;
            case "--repeats":
                repeats = int.Parse(args[i + 1]);
                break;
            case "--budget-mib":
                budgetMiB = int.Parse(args[i + 1]);
                break;
            case "--memory-budget-mib":
                memoryBudgetMiB = int.Parse(args[i + 1]);
                break;
            case "--engine":
                engine = args[i + 1];
                break;
            case "--output":
                output = args[i + 1];
                break;
            case "--run-id":
                runId = Guid.ParseExact(args[i + 1], "N");
                break;
            default:
                throw new ArgumentException($"Unknown option: {args[i]}");
        }
    if (
        count is < 100 or > 2_000_000
        || repeats is < 1 or > 10
        || budgetMiB is < 64 or > 2048
        || memoryBudgetMiB is < 64 or > 2048
        || engine
            is not (
                "all"
                or "compare-browser"
                or "sqlite"
                or "sqlite-browser"
                or "duckdb"
                or "duckdb-native"
                or "duckdb-memory"
            )
    )
        throw new ArgumentException("Invalid option value.");
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancellation.Cancel();
    };
    if (engine is not ("all" or "compare-browser"))
    {
        var result = Measurement.Run(
            engine,
            count,
            engine == "duckdb-memory" ? memoryBudgetMiB ?? budgetMiB : budgetMiB,
            cancellation.Token,
            runId
        );
        Console.WriteLine(JsonSerializer.Serialize(result));
        return 0;
    }
    string[] candidates =
        engine == "compare-browser"
            ? ["sqlite", "sqlite-browser", "duckdb-native"]
            : ["sqlite", "sqlite-browser", "duckdb", "duckdb-native", "duckdb-memory"];
    var runs = new List<RunResult>();
    for (int repeat = 0; repeat < repeats; repeat++)
    {
        foreach (string candidate in repeat % 2 == 0 ? candidates : candidates.Reverse().ToArray())
        {
            Guid workerId = Guid.NewGuid();
            string workerDirectory = Measurement.DirectoryFor(workerId);
            if (Directory.Exists(workerDirectory))
                throw new IOException("Benchmark directory already exists.");
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (
                string.Equals(
                    Path.GetFileNameWithoutExtension(Environment.ProcessPath),
                    "dotnet",
                    StringComparison.OrdinalIgnoreCase
                )
            )
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            foreach (
                string value in new[]
                {
                    "--engine",
                    candidate,
                    "--count",
                    count.ToString(),
                    "--budget-mib",
                    (
                        candidate == "duckdb-memory" ? memoryBudgetMiB ?? budgetMiB : budgetMiB
                    ).ToString(),
                    "--run-id",
                    workerId.ToString("N"),
                }
            )
                start.ArgumentList.Add(value);
            using var child =
                Process.Start(start)
                ?? throw new InvalidOperationException("Could not start benchmark worker.");
            var stdout = child.StandardOutput.ReadToEndAsync(cancellation.Token);
            var stderr = Task.Run(
                async () =>
                {
                    while (await child.StandardError.ReadLineAsync(cancellation.Token) is { } line)
                        Console.Error.WriteLine(line);
                },
                cancellation.Token
            );
            try
            {
                await child.WaitForExitAsync(cancellation.Token);
                await stderr;
            }
            catch
            {
                if (!child.HasExited)
                    child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                Measurement.DeleteOwned(workerDirectory);
                throw;
            }
            string json = await stdout;
            if (child.ExitCode != 0)
                throw new InvalidOperationException(
                    $"{candidate} failed with exit {child.ExitCode}: {json}"
                );
            runs.Add(
                JsonSerializer.Deserialize<RunResult>(json)
                    ?? throw new InvalidDataException("Worker returned no result.")
            );
            WriteReport(false);
        }
    }
    if (runs.Select(run => run.Fingerprint).Distinct().Count() != 1)
        throw new InvalidDataException("Stored facts differ across engines or runs.");
    if (runs.Select(run => run.BrowserFingerprint).Distinct().Count() != 1)
        throw new InvalidDataException("Complete browsing rows differ across engines or runs.");
    if (
        runs.Select(run =>
                JsonSerializer.Serialize(
                    run.Queries.ToDictionary(query => query.Name, query => query.Checksum)
                )
            )
            .Distinct()
            .Count() != 1
    )
        throw new InvalidDataException("Query results differ across engines or runs.");
    WriteReport(true);
    Console.WriteLine(
        $"Synthetic facts only; {count:N0} records + root per generation; 2 generations; {repeats} runs per engine; {budgetMiB} MiB budget; results and all table fingerprints match."
    );
    foreach (var group in runs.GroupBy(run => run.Engine))
    {
        Console.WriteLine(
            $"{group.Key}: ready={Median(group.Select(run => run.ReadySeconds)):F3}s; publish={Median(group.Select(run => run.PublishSeconds.Sum())):F3}s; checkpoint={Median(group.Select(run => run.CheckpointSeconds)):F3}s; file={Median(group.Select(run => (double)run.DatabaseBytes)) / 1048576:F1}MiB; peak={Median(group.Select(run => (double)run.PeakWorkingSetBytes)) / 1048576:F1}MiB"
        );
        foreach (var name in group.First().Queries.Select(query => query.Name))
            Console.WriteLine(
                $"  {name}: first={Median(group.Select(run => run.Queries.Single(query => query.Name == name).FirstMilliseconds)):F3}ms; warm-median={Median(group.Select(run => run.Queries.Single(query => query.Name == name).MedianMilliseconds)):F3}ms; warm-p95={Median(group.Select(run => run.Queries.Single(query => query.Name == name).P95Milliseconds)):F3}ms"
            );
    }
    return 0;

    void WriteReport(bool completed)
    {
        if (output is null)
            return;
        string path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var report = new
        {
            Completed = completed,
            GeneratedUtc = DateTimeOffset.UtcNow,
            RecordsPerGeneration = count + 1,
            Generations = 2,
            Repeats = repeats,
            BudgetMiB = budgetMiB,
            MemoryBudgetMiB = memoryBudgetMiB ?? budgetMiB,
            DurableFileDatabases = true,
            OsCacheCleared = false,
            Runs = runs,
        };
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })
        );
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Benchmark cancelled; owned test databases cleaned up.");
    return 130;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

static double Median(IEnumerable<double> values)
{
    double[] sorted = [.. values.Order()];
    return sorted[sorted.Length / 2];
}

namespace Something.StorageCompare
{
    internal sealed record QueryResult(
        string Name,
        double FirstMilliseconds,
        double MedianMilliseconds,
        double P95Milliseconds,
        ulong Checksum
    );

    internal sealed record RunResult(
        string Engine,
        int Count,
        int BudgetMiB,
        double SchemaSeconds,
        double[] PublishSeconds,
        double CheckpointSeconds,
        double ReadySeconds,
        long DatabaseBytes,
        long PeakWorkingSetBytes,
        long ProcessWrittenBytes,
        string Fingerprint,
        string BrowserFingerprint,
        QueryResult[] Queries
    );

    internal static class Measurement
    {
        internal static RunResult Run(
            string engine,
            int count,
            int budgetMiB,
            CancellationToken token,
            Guid runId
        )
        {
            string owned = DirectoryFor(runId);
            if (Directory.Exists(owned))
                throw new IOException("Benchmark directory already exists.");
            Directory.CreateDirectory(owned);
            string phase = "create-schema";
            var elapsed = Stopwatch.StartNew();
            using var heartbeat = new Timer(
                _ =>
                    Console.Error.WriteLine(
                        $"{engine}: elapsed={elapsed.Elapsed.TotalSeconds:F0}s; phase={Volatile.Read(ref phase)}"
                    ),
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5)
            );
            try
            {
                string path = Path.Combine(owned, "test.db");
                var watch = Stopwatch.StartNew();
                using var sqlite = engine is "sqlite" or "sqlite-browser"
                    ? ScanStore.OpenSqliteHistoryWriter(path, (ulong)budgetMiB * 1048576, token)
                    : null;
                using var duck = engine is not ("sqlite" or "sqlite-browser")
                    ? new DuckStore(
                        path,
                        budgetMiB,
                        engine is "duckdb-native" or "duckdb-memory",
                        engine == "duckdb-memory"
                    )
                    : null;
                if (engine == "sqlite-browser")
                    SqliteBrowser.Create(sqlite!.HistoryContext, token);
                double schema = watch.Elapsed.TotalSeconds;
                DbConnection connection =
                    (DbConnection?)sqlite?.HistoryContext.Database.GetDbConnection()
                    ?? duck!.Connection;
                double[] publish = new double[2];
                double checkpoint = 0;
                Guid[] ids = new Guid[2];
                for (int generation = 0; generation < 2; generation++)
                {
                    Volatile.Write(ref phase, $"generate-{generation}");
                    var buffer = SyntheticData.Create(count, generation, token);
                    Volatile.Write(ref phase, $"publish-{generation}");
                    watch.Restart();
                    if (engine == "duckdb-memory")
                        duck!.BeginNextGeneration();
                    var ended = SyntheticData.End.AddDays(generation == 0 ? -7 : 0);
                    ids[generation] = sqlite is not null
                        ? sqlite.PublishHistoryBuffer(
                            buffer,
                            ended,
                            new()
                            {
                                VolumeKey = SyntheticData.Volume,
                                Drive = "Synthetic",
                                CompletedUtcTicks = ended.UtcTicks,
                                LocalDay = DateOnly.FromDateTime(ended.UtcDateTime).DayNumber,
                            },
                            token,
                            engine == "sqlite-browser"
                                ? (context, id) => SqliteBrowser.Append(context, id, token)
                                : null
                        )
                        : duck!.Publish(buffer, generation, token);
                    publish[generation] = watch.Elapsed.TotalSeconds;
                    if (engine == "duckdb-memory")
                    {
                        Volatile.Write(ref phase, $"write-generation-{generation}");
                        watch.Restart();
                        duck!.WriteFile();
                        checkpoint += watch.Elapsed.TotalSeconds;
                    }
                }
                Volatile.Write(ref phase, "checkpoint");
                watch.Restart();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = engine is "sqlite" or "sqlite-browser"
                        ? "PRAGMA wal_checkpoint(TRUNCATE);"
                        : "CHECKPOINT;";
                    if (duck is not null && engine != "duckdb-memory")
                        duck.WriteFile();
                    else if (duck is null)
                        command.ExecuteNonQuery();
                }
                checkpoint += watch.Elapsed.TotalSeconds;
                long bytes = Directory
                    .EnumerateFiles(owned, "*", SearchOption.AllDirectories)
                    .Sum(file => new FileInfo(file).Length);
                Volatile.Write(ref phase, "queries");
                var queries = Workloads.Measure(
                    connection,
                    ids,
                    count,
                    token,
                    duck?.NativeSchema == true,
                    engine == "sqlite-browser"
                );
                Volatile.Write(ref phase, "verify-all-stored-columns");
                string fingerprint = Workloads.Fingerprint(connection, ids, token);
                string browserFingerprint = Workloads.BrowserFingerprint(
                    connection,
                    ids,
                    token,
                    duck?.NativeSchema == true || engine == "sqlite-browser"
                );
                Volatile.Write(ref phase, "verify-rollback");
                var failedBuffer = SyntheticData.Create(100, 1, token);
                if (duck is not null)
                {
                    using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
                    try
                    {
                        duck.Publish(failedBuffer, 1, cancelled.Token, cancelled.Cancel);
                        throw new InvalidOperationException(
                            "Cancelled DuckDB publication committed."
                        );
                    }
                    catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
                }
                else
                {
                    try
                    {
                        sqlite!.PublishHistoryBuffer(
                            failedBuffer,
                            SyntheticData.End,
                            new()
                            {
                                VolumeKey = SyntheticData.Volume,
                                Drive = "Synthetic",
                                CompletedUtcTicks = SyntheticData.End.UtcTicks,
                                LocalDay = -1,
                            },
                            token,
                            engine == "sqlite-browser"
                                ? (context, id) => SqliteBrowser.Append(context, id, token)
                                : null
                        );
                        throw new InvalidOperationException("Failed SQLite publication committed.");
                    }
                    catch (DbUpdateException) { }
                }
                using (var invariant = connection.CreateCommand())
                {
                    invariant.CommandText = "SELECT COUNT(*) FROM scan;";
                    if (Convert.ToInt64(invariant.ExecuteScalar()) != 2)
                        throw new InvalidDataException("Rollback left a new generation.");
                    invariant.CommandText = "SELECT ScanId FROM published_generation;";
                    if (
                        !Guid.TryParse(invariant.ExecuteScalar()?.ToString(), out var current)
                        || current != ids[1]
                    )
                        throw new InvalidDataException(
                            "Rollback changed the published generation."
                        );
                    if (engine == "sqlite-browser")
                    {
                        invariant.CommandText =
                            "SELECT COUNT(*) FROM browser b WHERE NOT EXISTS (SELECT 1 FROM scan s WHERE s.Id=b.ScanId);";
                        if (Convert.ToInt64(invariant.ExecuteScalar()) != 0)
                            throw new InvalidDataException("Rollback left derived browsing rows.");
                    }
                }
                using var process = Process.GetCurrentProcess();
                return new(
                    engine,
                    count,
                    budgetMiB,
                    schema,
                    publish,
                    checkpoint,
                    schema + publish.Sum() + checkpoint,
                    bytes,
                    process.PeakWorkingSet64,
                    ProcessIo.WrittenBytes(process),
                    fingerprint,
                    browserFingerprint,
                    queries
                );
            }
            finally
            {
                DeleteOwned(owned);
            }
        }

        internal static string DirectoryFor(Guid id) =>
            Path.Combine(Path.GetTempPath(), "something-storage-compare-" + id.ToString("N"));

        internal static void DeleteOwned(string owned)
        {
            const string prefix = "something-storage-compare-";
            string root =
                Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string name = Path.GetFileName(owned);
            if (
                !Path.GetFullPath(owned).StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !name.StartsWith(prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(name[prefix.Length..], "N", out _)
            )
                throw new InvalidOperationException("Invalid cleanup path.");
            if (Directory.Exists(owned))
                Directory.Delete(owned, recursive: true);
        }
    }
}
