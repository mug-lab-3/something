using System.Diagnostics;
using Something.Ntfs;
using Something.Statistics;
using Something.Storage;

// Synthetic owned facts only: no volume access, elevation or real file names.
internal static class StorageBenchmark
{
    public static int Run(int count, bool buffered = false, bool memoryDatabase = false)
    {
        if (count is < 1 or > 2_000_000)
            throw new ArgumentOutOfRangeException(nameof(count));
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        using var temporary = TemporaryScanDatabase.Create(
            cancellation.Token,
            inMemory: memoryDatabase
        );
        var store = temporary.Store;
        var root = FileReference.Create(5, 1);
        var descriptor = new ScanDescriptor(
            "synthetic-benchmark",
            Guid.NewGuid(),
            1,
            new(0, (ulong)count + 16),
            DateTimeOffset.UtcNow
        );
        var buffer = buffered ? new BufferedScan(descriptor) : null;
        var id = buffered ? Guid.Empty : store.BeginScan(descriptor, cancellation.Token);
        var watch = Stopwatch.StartNew();
        long generated = 0;
        string phase = "collect-or-stage";
        using var heartbeat = new Timer(
            _ =>
                Console.Error.WriteLine(
                    $"Elapsed={watch.Elapsed.TotalSeconds:F0}s; phase={Volatile.Read(ref phase)}; records={Interlocked.Read(ref generated)}"
                ),
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5)
        );
        var batch = new List<StagedObservation>(ScanStore.MaximumBatchSize);
        void Add(ulong index, bool directory, FileReference parent)
        {
            var reference = FileReference.Create(index, 1);
            var item = new StagedObservation(
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
            );
            if (buffer is not null)
                buffer.Add(item, cancellation.Token);
            else
                batch.Add(item);
            Interlocked.Increment(ref generated);
            if (batch.Count == ScanStore.MaximumBatchSize)
                Flush();
        }
        void Flush()
        {
            store.StageBatch(id, batch, cancellation.Token);
            batch.Clear();
        }
        Console.Error.WriteLine(
            $"Synthetic storage benchmark: {count} records; memory-db={memoryDatabase}; temporary DB; Ctrl+C cancels."
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
        Volatile.Write(ref phase, "aggregate-and-publish");
        Console.Error.WriteLine(
            $"Collected/staged in {stage.TotalSeconds:F3}s; aggregating and publishing."
        );
        var result = buffer is null
            ? store.CompleteScan(id, DateTimeOffset.UtcNow, cancellation.Token)
            : buffer.Complete(cancellation.Token);
        var aggregate = watch.Elapsed - stage;
        var persistStart = watch.Elapsed;
        if (buffer is not null)
            id = store.PublishBufferedScan(buffer, DateTimeOffset.UtcNow, cancellation.Token);
        var complete = watch.Elapsed - stage;
        var persist = watch.Elapsed - persistStart;
        var export = TimeSpan.Zero;
        long snapshotBytes = 0;
        if (memoryDatabase)
        {
            Volatile.Write(ref phase, "export-database");
            var exportStart = watch.Elapsed;
            snapshotBytes = temporary.ExportSnapshot(cancellation.Token);
            export = watch.Elapsed - exportStart;
        }
        var measuredTotal = watch.Elapsed;
        long expected = (long)(count - (count + 9) / 10) * 4096;
        if (
            result.Folders[root].SubtreeData
                != new CapacityAmount((UInt128)expected, (UInt128)expected)
            || result.Unattributed.KnownData != default
            || store.ReadAggregation(id, cancellation.Token).Folders[root].SubtreeData
                != result.Folders[root].SubtreeData
        )
            throw new InvalidDataException("Synthetic benchmark capacity mismatch.");
        if (memoryDatabase)
        {
            using var snapshot = temporary.OpenSnapshot(cancellation.Token);
            if (
                snapshot.ReadAggregation(id, cancellation.Token).Folders[root].SubtreeData
                    != result.Folders[root].SubtreeData
                || snapshot.GetPublishedScan(descriptor.VolumeKey, descriptor.Range) != id
            )
                throw new InvalidDataException(
                    "Exported snapshot differs from the in-memory database."
                );
        }
        Console.WriteLine(
            $"Records={count}; buffered={buffered}; memory-db={memoryDatabase}; collect-or-stage-seconds={stage.TotalSeconds:F3}; aggregate-publish-seconds={complete.TotalSeconds:F3}; total-seconds={measuredTotal.TotalSeconds:F3}; capacity-match=True"
        );
        if (buffered)
            Console.WriteLine(
                $"Timing: aggregate-seconds={aggregate.TotalSeconds:F3}; persist-publish-seconds={persist.TotalSeconds:F3}; db-snapshot-seconds={export.TotalSeconds:F3}; snapshot-bytes={snapshotBytes}"
            );
        else if (memoryDatabase)
            Console.WriteLine(
                $"Snapshot: db-snapshot-seconds={export.TotalSeconds:F3}; snapshot-bytes={snapshotBytes}"
            );
        using var process = Process.GetCurrentProcess();
        Console.WriteLine(
            $"Memory: process-lifetime-peak-working-set-bytes={process.PeakWorkingSet64}"
        );
        return 0;
    }
}
