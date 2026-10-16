using System.Diagnostics;
using System.Text.Json;
using Something.Statistics;
using Something.Storage;

namespace Something.StorageCompare;

internal static class VisibilityBenchmark
{
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
        if (args.Length > 2)
            throw new ArgumentException("Expected [count] [budgetMiB].", nameof(args));
        int count = args.Length == 0 ? 100_000 : int.Parse(args[0]);
        int budgetMiB = args.Length < 2 ? 1024 : int.Parse(args[1]);
        if (budgetMiB is < 1024 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(args));
        if (count is < 100 or > 2_000_000)
            throw new ArgumentOutOfRangeException(nameof(args));
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        var token = cancellation.Token;
        string directory = Path.GetFullPath(
            Path.Combine("artifacts", "visibility-benchmark", Guid.NewGuid().ToString("N"))
        );
        var buffer = HistoryDedupData.Create(count, 0, "mixed", token);
        var archive = new ScanHistoryArchive(directory);
        Console.WriteLine(
            $"Publishing {count:N0} synthetic files; no live disk or user history is read."
        );
        var entry = archive.Publish(
            buffer,
            DateTimeOffset.UtcNow,
            "Synthetic",
            TimeSpan.FromSeconds(1),
            (ulong)budgetMiB << 20,
            token
        );
        using var snapshot = archive.Open(entry.Id, token);
        var store = snapshot.Store;
        var root = store.ReadSnapshotSummary(entry.Id, token).Root;
        Console.WriteLine("Reading full facts…");
        var watch = Stopwatch.StartNew();
        int files = store.ReadFiles(entry.Id, token).Count();
        double readSeconds = watch.Elapsed.TotalSeconds;
        Console.WriteLine(
            $"Read {files:N0} observations in {readSeconds:F3}s; building visibility projection…"
        );
        watch.Restart();
        var projection = new VisibleScan(
            root.Reference,
            () => store.ReadFiles(entry.Id, token),
            new(false, false),
            token,
            store.ReadVisibilityFolders(entry.Id, token)
        );
        double visibilitySeconds = watch.Elapsed.TotalSeconds;
        Console.WriteLine(
            $"Projection completed in {visibilitySeconds:F3}s; reading the complete root listing…"
        );
        watch.Restart();
        int rootEntries = store.ReadAllFolderEntries(entry.Id, root.Reference, token).Length;
        double listingSeconds = watch.Elapsed.TotalSeconds;
        bool match = root.Entries == projection.Aggregation.Folders[root.Reference].Entries;
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    Count = count,
                    BudgetMiB = budgetMiB,
                    Observations = files,
                    ReadSeconds = readSeconds,
                    VisibilitySeconds = visibilitySeconds,
                    RootEntries = rootEntries,
                    ListingSeconds = listingSeconds,
                    TotalsMatch = match,
                }
            )
        );
        return match ? 0 : 1;
    }
}
