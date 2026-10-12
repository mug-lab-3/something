using System.Diagnostics;
using Something.Ntfs;
using Something.Windows;

internal static class ScanBenchmark
{
    public static async Task<int> RunAsync(
        WindowsMftRecordSource source,
        MftScanRange range,
        int maximumRecords,
        CancellationToken token
    )
    {
        var state = new State(range);
        var clock = Stopwatch.StartNew();
        long allocatedAtStart = GC.GetTotalAllocatedBytes(precise: true);
        using var heartbeatCancellation = new CancellationTokenSource();
        Console.Error.WriteLine(
            $"Scanning {range.RecordCount} observed slots; maximum-records={maximumRecords}; progress every 5s; Ctrl+C cancels."
        );
        Task heartbeat = HeartbeatAsync(state, clock, heartbeatCancellation.Token);
        bool completed = false;
        try
        {
            // Native acquisition is synchronous. Keep the heartbeat responsive during a native call.
            await Task.Run(
                async () =>
                {
                    await foreach (
                        var entry in new LiveMftScanner(
                            source.Volume.BytesPerCluster,
                            source.Volume.TotalClusters,
                            maximumRecords
                        ).ScanAsync(source, range, token)
                    )
                        state.Add(entry);
                },
                token
            );
            completed = true;
        }
        finally
        {
            clock.Stop();
            long managedAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedAtStart;
            heartbeatCancellation.Cancel();
            await heartbeat;
            state.Print(clock.Elapsed, source.Metrics, source.Volume, completed);
            using var process = Process.GetCurrentProcess();
            Console.WriteLine(
                $"Memory: managed-allocated-during-scan-bytes={managedAllocated}; process-lifetime-peak-working-set-bytes={process.PeakWorkingSet64}"
            );
        }
        return state.Unresolved == 0 ? 0 : 1;
    }

    private static async Task HeartbeatAsync(State state, Stopwatch clock, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(token))
                state.PrintProgress(clock.Elapsed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private sealed class State(MftScanRange range)
    {
        private readonly object gate = new();
        private readonly Dictionary<LiveFileReadStatus, ulong> outcomes = [];
        private readonly MftScanIssueSampler issueSamples = new();
        private ulong candidates;
        private ulong files;
        private ulong directories;
        private ulong extensions;
        private ulong? lastIndex;
        private UInt128 logical;
        private UInt128 allocated;
        private UInt128 fileLogical;
        private UInt128 fileAllocated;
        public ulong Unresolved { get; private set; }

        public void Add(MftScanEntry entry)
        {
            lock (gate)
            {
                candidates++;
                lastIndex = entry.RecordIndex;
                outcomes[entry.Status] = outcomes.GetValueOrDefault(entry.Status) + 1;
                if (entry.Kind == MftScanEntryKind.ExtensionRecord)
                    extensions++;
                else if (entry.File is { } file)
                {
                    if (file.IsDirectory)
                        directories++;
                    else
                        files++;
                    foreach (var stream in file.DataStreams)
                    {
                        logical = checked(logical + (UInt128)stream.LogicalBytes);
                        allocated = checked(allocated + (UInt128)stream.AllocatedBytes);
                        if (!file.IsDirectory)
                        {
                            fileLogical = checked(fileLogical + (UInt128)stream.LogicalBytes);
                            fileAllocated = checked(fileAllocated + (UInt128)stream.AllocatedBytes);
                        }
                    }
                }
                else
                {
                    Unresolved++;
                    issueSamples.Add(entry);
                }
            }
        }

        public void PrintProgress(TimeSpan elapsed)
        {
            string message;
            lock (gate)
            {
                ulong traversed = lastIndex is { } index ? range.EndExclusive - index : 0;
                message =
                    $"Elapsed={elapsed.TotalSeconds:F0}s; traversed={traversed}/{range.RecordCount}; candidates={candidates}; unresolved={Unresolved}; last-completed-record={lastIndex?.ToString() ?? "none"}";
            }
            Console.Error.WriteLine(message);
        }

        public void Print(
            TimeSpan elapsed,
            VolumeReadMetrics metrics,
            NtfsVolumeInfo volume,
            bool completed
        )
        {
            // Worker and heartbeat have both stopped. Final output is outside the measured interval.
            ulong traversed =
                completed ? range.RecordCount
                : lastIndex is { } index ? range.EndExclusive - index
                : 0;
            Console.WriteLine(
                $"Scan completed={completed}; observed-slots={range.RecordCount}; traversed-slots={traversed}; candidates={candidates}; slots-not-returned={traversed - candidates}"
            );
            Console.WriteLine(
                $"Resolved-files={files}; resolved-directories={directories}; extension-records={extensions}; unresolved={Unresolved}"
            );
            Console.WriteLine(
                $"Elapsed={elapsed.TotalSeconds:F3}s; candidates-per-second={(elapsed.TotalSeconds > 0 ? candidates / elapsed.TotalSeconds : 0):F1}; native-read-time={metrics.AcquisitionTime.TotalSeconds:F3}s; record-requests={metrics.RecordRequests}; raw-reads={metrics.RawReads}"
            );
            foreach (var (status, count) in outcomes.OrderBy(p => p.Key))
            {
                Console.WriteLine($"  {status}={count}");
                var samples = issueSamples.GetSamples(status);
                for (int i = 0; i < samples.Length; i++)
                {
                    var sample = samples[i];
                    Console.WriteLine(
                        $"    {(i == 0 ? "First unresolved" : "Unresolved sample")} record={sample.RecordIndex}; reason={sample.Error ?? "not available"}"
                    );
                }
                if (samples.Length != 0 && count > (ulong)samples.Length)
                    Console.WriteLine(
                        $"    Additional unresolved examples omitted={count - (ulong)samples.Length}"
                    );
            }
            Console.WriteLine(
                $"Resolved-DATA-only: logical-bytes={logical}; allocated-bytes={allocated}"
            );
            Console.WriteLine(
                $"Resolved-regular-files-DATA: files={files}; logical-bytes={fileLogical}; allocated-bytes={fileAllocated}"
            );
            UInt128 volumeUsed = checked(
                (UInt128)(volume.TotalClusters - volume.FreeClusters) * (uint)volume.BytesPerCluster
            );
            Console.WriteLine(
                $"Volume-at-open: used-bytes={volumeUsed}; DATA-allocated-bytes={allocated}"
            );
            Console.WriteLine(
                allocated <= volumeUsed
                    ? $"Volume-used-minus-resolved-DATA-allocated-bytes={volumeUsed - allocated}"
                    : $"Resolved-DATA-allocated-exceeds-volume-used-bytes={allocated - volumeUsed}"
            );
            Console.WriteLine(
                "Optimistic observations; gaps are not confirmed deletions. DATA totals exclude unresolved files and metadata overhead; they are not disk usage. No scan data is saved."
            );
        }
    }
}
