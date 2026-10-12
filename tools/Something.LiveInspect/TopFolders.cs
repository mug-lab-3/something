using System.Diagnostics;
using System.Globalization;
using Something.Ntfs;
using Something.Storage;
using Something.Windows;

internal static class TopFolders
{
    public static async Task<int> RunAsync(
        WindowsMftRecordSource source,
        string drive,
        int maximumRecords,
        CancellationToken token
    )
    {
        var watch = Stopwatch.StartNew();
        long candidates = 0;
        string phase = "scan-and-stage";
        using var heartbeatCancellation = new CancellationTokenSource();
        Console.Error.WriteLine(
            "Scanning for top 5 folders; progress every 5s; Ctrl+C cancels. A temporary SQLite database is used."
        );
        var heartbeat = Task.Run(
            async () =>
            {
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
                    while (await timer.WaitForNextTickAsync(heartbeatCancellation.Token))
                        Console.Error.WriteLine(
                            $"Elapsed={watch.Elapsed.TotalSeconds:F0}s; phase={Volatile.Read(ref phase)}; candidates={Interlocked.Read(ref candidates)}"
                        );
                }
                catch (OperationCanceledException)
                    when (heartbeatCancellation.IsCancellationRequested) { }
            },
            CancellationToken.None
        );
        try
        {
            // Source acquisition and SQLite calls are synchronous; keep them off the heartbeat thread.
            return await Task.Run(
                async () =>
                {
                    using var temporary = TemporaryScanDatabase.Create(token);
                    var store = temporary.Store;
                    Guid scope = Guid.NewGuid();
                    // Valid only for this single opened volume and disposable database. No cross-scan identity claim.
                    var descriptor = new ScanDescriptor(
                        $"diagnostic:{scope:N}:{source.Volume.SerialNumber:X16}",
                        scope,
                        source.Volume.SerialNumber,
                        new(0, source.Volume.ObservedRecordCount),
                        DateTimeOffset.UtcNow
                    );
                    var id = store.BeginScan(descriptor, token);
                    var batch = new List<StagedObservation>(ScanStore.MaximumBatchSize);
                    await foreach (
                        var entry in new LiveMftScanner(
                            source.Volume.BytesPerCluster,
                            source.Volume.TotalClusters,
                            maximumRecords
                        ).ScanAsync(source, descriptor.Range, token)
                    )
                    {
                        batch.Add(StagedObservation.FromEntry(entry, token));
                        Interlocked.Increment(ref candidates);
                        if (batch.Count == ScanStore.MaximumBatchSize)
                        {
                            store.StageBatch(id, batch, token);
                            batch.Clear();
                        }
                    }
                    if (batch.Count != 0)
                        store.StageBatch(id, batch, token);
                    Volatile.Write(ref phase, "aggregate-and-publish");
                    var result = store.CompleteScan(id, DateTimeOffset.UtcNow, token);
                    Volatile.Write(ref phase, "rank-folders");
                    var top = store.ReadLargestFolders(id, cancellationToken: token);
                    bool incomplete =
                        result.HasUnknownAttribution
                        || result.Unattributed.UnresolvedCount != 0
                        || result.Folders.Values.Any(f =>
                            f.SubtreeUnresolvedCount != 0 || f.SubtreeUnobservedDirectoryCount != 0
                        );
                    Console.WriteLine(
                        $"Top {top.Length} folders by known allocated DATA in subtree (root excluded):"
                    );
                    int rank = 0;
                    foreach (var item in top)
                    {
                        token.ThrowIfCancellationRequested();
                        string size = (
                            (double)item.Sample.SubtreeData.AllocatedBytes / (1024 * 1024 * 1024)
                        ).ToString("F3", CultureInfo.InvariantCulture);
                        string path = string.Concat(
                            item.RelativePath.Select(c =>
                                char.IsControl(c) || char.IsSurrogate(c)
                                    ? $"\\u{(int)c:X4}"
                                    : c.ToString()
                            )
                        );
                        Console.WriteLine(
                            $"{++rank}. {drive}\\{path} | {size} GiB | allocated-bytes={item.Sample.SubtreeData.AllocatedBytes}; logical-bytes={item.Sample.SubtreeData.LogicalBytes}; subtree-unresolved={item.Sample.SubtreeUnresolvedCount}"
                        );
                    }
                    Console.WriteLine(
                        $"Elapsed={watch.Elapsed.TotalSeconds:F3}s; incomplete={incomplete}; metadata-unresolved={result.Metadata.UnresolvedCount}; unattributed-unresolved={result.Unattributed.UnresolvedCount}"
                    );
                    Console.WriteLine(
                        "Optimistic DATA observations only; nested rows overlap, metadata overhead is excluded, and unresolved capacity can change ranking. No history is retained."
                    );
                    return incomplete ? 1 : 0;
                },
                token
            );
        }
        finally
        {
            heartbeatCancellation.Cancel();
            await heartbeat;
        }
    }
}
