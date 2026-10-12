using System.Diagnostics;
using Something.Ntfs;
using Something.Statistics;
using Something.Storage;

// Synthetic owned facts only: no volume access, elevation or real file names.
internal static class StorageBenchmark
{
    public static int Run(int count)
    {
        if (count is < 1 or > 2_000_000)
            throw new ArgumentOutOfRangeException(nameof(count));
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        using var temporary = TemporaryScanDatabase.Create(cancellation.Token);
        var store = temporary.Store;
        var root = FileReference.Create(5, 1);
        var id = store.BeginScan(
            new(
                "synthetic-benchmark",
                Guid.NewGuid(),
                1,
                new(0, (ulong)count + 16),
                DateTimeOffset.UtcNow
            ),
            cancellation.Token
        );
        var watch = Stopwatch.StartNew();
        var batch = new List<StagedObservation>(ScanStore.MaximumBatchSize);
        void Add(ulong index, bool directory, FileReference parent)
        {
            var reference = FileReference.Create(index, 1);
            batch.Add(
                new(
                    index,
                    MftScanEntryKind.ResolvedFile,
                    LiveFileReadStatus.ObservedUnchanged,
                    new(
                        reference,
                        directory,
                        CapacityDomain.Ordinary,
                        CapacityResolution.Resolved,
                        [
                            new(parent, $"synthetic-{index}", FileNameNamespace.Win32, 0),
                            new(parent, $"SHORT-{index}", FileNameNamespace.Dos, 0),
                        ],
                        directory ? [] : [new("", 4096, 4096)],
                        true
                    )
                    {
                        ObservedHardLinkCount = 2,
                    }
                )
            );
            if (batch.Count == ScanStore.MaximumBatchSize)
                Flush();
        }
        void Flush()
        {
            store.StageBatch(id, batch, cancellation.Token);
            batch.Clear();
        }
        Console.Error.WriteLine(
            $"Synthetic storage benchmark: {count} records; temporary DB; Ctrl+C cancels."
        );
        Add(5, true, root);
        for (int i = 0; i < count; i++)
            Add(
                (ulong)i + 16,
                i % 10 == 0,
                i % 10 == 0 ? root : FileReference.Create((ulong)(i / 10 * 10) + 16, 1)
            );
        if (batch.Count != 0)
            Flush();
        var stage = watch.Elapsed;
        Console.Error.WriteLine($"Staged in {stage.TotalSeconds:F3}s; aggregating and publishing.");
        var result = store.CompleteScan(id, DateTimeOffset.UtcNow, cancellation.Token);
        var complete = watch.Elapsed - stage;
        long expected = (long)(count - (count + 9) / 10) * 4096;
        if (
            result.Folders[root].SubtreeData
                != new CapacityAmount((UInt128)expected, (UInt128)expected)
            || result.Unattributed.KnownData != default
        )
            throw new InvalidDataException("Synthetic benchmark capacity mismatch.");
        Console.WriteLine(
            $"Records={count}; stage-seconds={stage.TotalSeconds:F3}; aggregate-publish-seconds={complete.TotalSeconds:F3}; total-seconds={watch.Elapsed.TotalSeconds:F3}; capacity-match=True"
        );
        return 0;
    }
}
