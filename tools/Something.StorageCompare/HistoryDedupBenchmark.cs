using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DuckDB.NET.Data;

namespace Something.StorageCompare;

internal static class HistoryDedupBenchmark
{
    private sealed record GenerationResult(
        int Generation,
        double PublishSeconds,
        double CheckpointSeconds,
        long DatabaseBytes,
        long WalBytes,
        long NewFacts,
        long NewListings
    );

    private sealed record Result(
        string Mode,
        string Shape,
        int Count,
        int BudgetMiB,
        GenerationResult[] Generations,
        QueryResult[] LatestQueries,
        QueryResult[] OldQueries,
        string Facts,
        string Browser,
        string RetainedFacts,
        string RetainedBrowser,
        long PeakWorkingSetBytes,
        long WrittenBytes
    );

    internal static async Task<int> RunAsync(string[] args)
    {
        int count = 100_000,
            generations = 5,
            budget = 2048;
        string mode = "all",
            shape = "mixed";
        string? output = null;
        Guid? worker = null;
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        try
        {
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
                        budget = int.Parse(args[i + 1]);
                        break;
                    case "--mode":
                        mode = args[i + 1];
                        break;
                    case "--shape":
                        shape = args[i + 1];
                        break;
                    case "--output":
                        output = args[i + 1];
                        break;
                    case "--worker":
                        worker = Guid.ParseExact(args[i + 1], "N");
                        break;
                    default:
                        throw new ArgumentException($"Unknown option {args[i]}");
                }
            }
            if (
                count is < 100 or > 2_000_000
                || generations is < 2 or > 365
                || budget is < 1024 or > 8192
                || shape is not ("mixed" or "wide")
                || mode
                    is not ("all" or "full" or "reference" or "folders" or "blocks" or "production")
            )
                throw new ArgumentException("Invalid history comparison options.");
            if (worker is { } id)
            {
                Console.WriteLine(
                    JsonSerializer.Serialize(
                        Measure(mode, shape, count, generations, budget, id, cancellation.Token)
                    )
                );
                return 0;
            }
            string[] modes =
                mode == "all" ? ["full", "reference", "folders", "blocks", "production"] : [mode];
            var results = new List<Result>();
            foreach (string candidate in modes)
            {
                Guid workerId = Guid.NewGuid();
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
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
                    string arg in new[]
                    {
                        "--history-dedup",
                        "--mode",
                        candidate,
                        "--shape",
                        shape,
                        "--count",
                        count.ToString(),
                        "--generations",
                        generations.ToString(),
                        "--budget-mib",
                        budget.ToString(),
                        "--worker",
                        workerId.ToString("N"),
                    }
                )
                    start.ArgumentList.Add(arg);
                using var process =
                    Process.Start(start) ?? throw new IOException("Could not start benchmark.");
                var stdout = process.StandardOutput.ReadToEndAsync(cancellation.Token);
                var stderr = Task.Run(
                    async () =>
                    {
                        while (
                            await process.StandardError.ReadLineAsync(cancellation.Token)
                                is { } line
                        )
                            Console.Error.WriteLine(line);
                    },
                    cancellation.Token
                );
                try
                {
                    await process.WaitForExitAsync(cancellation.Token);
                    await stderr;
                }
                catch
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    DeleteOwned(workerId);
                    throw;
                }
                string json = await stdout;
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"{candidate} worker failed: {json}");
                results.Add(
                    JsonSerializer.Deserialize<Result>(json)
                        ?? throw new InvalidDataException("Missing result.")
                );
                if (output is not null)
                    WriteReport(output, results, false);
            }
            if (
                results.Select(r => r.Facts).Distinct().Count() != 1
                || results.Select(r => r.Browser).Distinct().Count() != 1
                || results.Select(r => r.RetainedFacts).Distinct().Count() != 1
                || results.Select(r => r.RetainedBrowser).Distinct().Count() != 1
            )
                throw new InvalidDataException("Full facts/browser fingerprints do not match.");
            foreach (var result in results.Skip(1))
            foreach (
                var (actual, expected) in result
                    .LatestQueries.Concat(result.OldQueries)
                    .Zip(results[0].LatestQueries.Concat(results[0].OldQueries))
            )
                if (actual.Name != expected.Name || actual.Checksum != expected.Checksum)
                    throw new InvalidDataException("Query results do not match.");
            if (output is not null)
                WriteReport(output, results, true);
            Console.WriteLine(
                $"All persisted facts, browsing rows, query results and retained generations match; {shape}, {count:N0} observations + root, {generations} generations."
            );
            foreach (var result in results)
            {
                Console.WriteLine(
                    $"{result.Mode}: publish={result.Generations.Sum(g => g.PublishSeconds):F3}s; final={result.Generations[^1].DatabaseBytes / 1048576.0:F1}MiB; peak={result.PeakWorkingSetBytes / 1048576.0:F1}MiB; written={result.WrittenBytes / 1048576.0:F1}MiB"
                );
                foreach (var gen in result.Generations)
                {
                    if (
                        result.Generations.Length > 20
                        && gen.Generation is >= 6
                        && gen.Generation < result.Generations.Length - 2
                    )
                    {
                        if (gen.Generation == 6)
                            Console.WriteLine(
                                "  Intermediate generations omitted; the JSON report contains every generation."
                            );
                        continue;
                    }
                    Console.WriteLine(
                        $"  generation={gen.Generation}; publish={gen.PublishSeconds:F3}s; file={gen.DatabaseBytes / 1048576.0:F1}MiB; new-facts={gen.NewFacts}; new-listings={gen.NewListings}"
                    );
                }
                foreach (var query in result.LatestQueries)
                    Console.WriteLine(
                        $"  {query.Name}: first={query.FirstMilliseconds:F3}ms; warm={query.MedianMilliseconds:F3}ms; p95={query.P95Milliseconds:F3}ms"
                    );
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Comparison cancelled.");
            return 130;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void WriteReport(string path, List<Result> results, bool complete)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new
                {
                    Complete = complete,
                    GeneratedUtc = DateTimeOffset.UtcNow,
                    OsCacheCleared = false,
                    Durable = true,
                    Results = results,
                },
                new JsonSerializerOptions { WriteIndented = true }
            )
        );
    }

    private static Result Measure(
        string mode,
        string shape,
        int count,
        int generations,
        int budget,
        Guid worker,
        CancellationToken token
    )
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "history-compare"));
        string directory = Path.Combine(root, worker.ToString("N"));
        if (Directory.Exists(directory))
            throw new IOException("Benchmark directory already exists.");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "synthetic.duckdb");
        string phase = "schema";
        int generation = -1;
        using var watchProcess = Process.GetCurrentProcess();
        long writtenBefore = ProcessIo.WrittenBytes(watchProcess);
        using var heartbeat = new Timer(
            _ =>
                Console.Error.WriteLine(
                    $"{mode}: generation={Volatile.Read(ref generation)}; phase={Volatile.Read(ref phase)}"
                ),
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5)
        );
        try
        {
            var history = new List<GenerationResult>();
            var ids = new List<Guid>();
            using var store = new HistoryDedupStore(path, mode, budget, token);
            for (generation = 0; generation < generations; generation++)
            {
                token.ThrowIfCancellationRequested();
                phase = "generate-and-aggregate";
                var buffer = HistoryDedupData.Create(count, generation, shape, token);
                phase = "publish-including-change-detection";
                var watch = Stopwatch.StartNew();
                ids.Add(store.Publish(buffer, token));
                double elapsed = watch.Elapsed.TotalSeconds;
                long wal = File.Exists(path + ".wal") ? new FileInfo(path + ".wal").Length : 0;
                phase = "checkpoint";
                watch.Restart();
                store.Checkpoint();
                history.Add(
                    new(
                        generation,
                        elapsed,
                        watch.Elapsed.TotalSeconds,
                        new FileInfo(path).Length,
                        wal,
                        store.NewFacts,
                        store.NewListings
                    )
                );
            }
            generation = generations - 1;
            store.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            using var connection = new DuckDBConnection($"Data Source={path}");
            connection.Open();
            using (var settings = connection.CreateCommand())
            {
                settings.CommandText =
                    $"SET memory_limit='{budget}MiB'; SET threads=4; SET preserve_insertion_order=false";
                settings.ExecuteNonQuery();
            }
            phase = "latest-query-after-reopen";
            var latest = Workloads.Measure(
                connection,
                [ids[0], ids[^1]],
                count,
                token,
                native: true
            );
            phase = "old-query";
            var old = Workloads.Measure(connection, [ids[0], ids[0]], count, token, native: true);
            phase = "full-fingerprint";
            string facts = Workloads.Fingerprint(connection, [.. ids], token);
            string browser = Workloads.BrowserFingerprint(
                connection,
                [.. ids],
                token,
                browser: true
            );
            phase = "delete-first-generation-and-verify";
            HistoryDedupStore.DeleteGeneration(connection, mode, ids[0]);
            string retained = Workloads.Fingerprint(connection, [.. ids.Skip(1)], token);
            string retainedBrowser = Workloads.BrowserFingerprint(
                connection,
                [.. ids.Skip(1)],
                token,
                browser: true
            );
            watchProcess.Refresh();
            return new(
                mode,
                shape,
                count,
                budget,
                [.. history],
                latest,
                old,
                facts,
                browser,
                retained,
                retainedBrowser,
                watchProcess.PeakWorkingSet64,
                ProcessIo.WrittenBytes(watchProcess) - writtenBefore
            );
        }
        finally
        {
            DeleteOwned(worker);
        }
    }

    private static void DeleteOwned(Guid worker)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "history-compare"));
        string resolved = Path.Combine(root, worker.ToString("N"));
        if (!Directory.Exists(resolved))
            return;
        if (
            !resolved.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            )
            || Path.GetFileName(resolved) != worker.ToString("N")
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0
        )
            throw new InvalidOperationException("Invalid owned benchmark cleanup path.");
        Directory.Delete(resolved, recursive: true);
    }
}
