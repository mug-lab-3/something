using System.Diagnostics;
using System.Globalization;
using Something.Ntfs;
using Something.Statistics;
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
        string phase = "scan-and-buffer";
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
                    Guid scope = Guid.NewGuid();
                    // Valid only for this single opened volume and disposable database. No cross-scan identity claim.
                    var descriptor = new ScanDescriptor(
                        $"diagnostic:{scope:N}:{source.Volume.SerialNumber:X16}",
                        scope,
                        source.Volume.SerialNumber,
                        new(0, source.Volume.ObservedRecordCount),
                        DateTimeOffset.UtcNow
                    );
                    var buffer = new BufferedScan(descriptor);
                    CapacityAmount observed = default;
                    long headerNameMismatches = 0;
                    long completeMismatchedNameSets = 0;
                    long scanStarted = Stopwatch.GetTimestamp();
                    await foreach (
                        var entry in new LiveMftScanner(
                            source.Volume.BytesPerCluster,
                            source.Volume.TotalClusters,
                            maximumRecords
                        ).ScanAsync(source, descriptor.Range, token)
                    )
                    {
                        if (entry.File is { } file)
                            foreach (var stream in file.DataStreams)
                                observed += new CapacityAmount(
                                    checked((UInt128)stream.LogicalBytes),
                                    checked((UInt128)stream.AllocatedBytes)
                                );
                        var staged = StagedObservation.FromEntry(entry, token);
                        if (
                            staged.File
                                is {
                                    Resolution: CapacityResolution.Resolved,
                                    ObservedHardLinkCount: { } headerCount
                                } stagedFile
                            && headerCount != stagedFile.Names.Length
                        )
                        {
                            headerNameMismatches++;
                            if (stagedFile.LinkSetComplete)
                                completeMismatchedNameSets++;
                        }
                        buffer.Add(staged, token);
                        Interlocked.Increment(ref candidates);
                    }
                    var scanTime = Stopwatch.GetElapsedTime(scanStarted);
                    long bufferedManagedBytes = GC.GetTotalMemory(forceFullCollection: false);
                    Volatile.Write(ref phase, "aggregate-buffer");
                    long completionStarted = Stopwatch.GetTimestamp();
                    var result = buffer.Complete(token);
                    var completionTime = Stopwatch.GetElapsedTime(completionStarted);
                    Volatile.Write(ref phase, "persist-and-publish");
                    long persistenceStarted = Stopwatch.GetTimestamp();
                    using var temporary = TemporaryScanDatabase.Create(token);
                    var store = temporary.Store;
                    var id = store.PublishBufferedScan(buffer, DateTimeOffset.UtcNow, token);
                    var persistenceTime = Stopwatch.GetElapsedTime(persistenceStarted);
                    Volatile.Write(ref phase, "rank-folders");
                    long rankingStarted = Stopwatch.GetTimestamp();
                    var top = store.ReadLargestFolders(id, cancellationToken: token);
                    var rankingTime = Stopwatch.GetElapsedTime(rankingStarted);
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
                        $"Timing: scan-and-buffer-seconds={scanTime.TotalSeconds:F3}; aggregate-buffer-seconds={completionTime.TotalSeconds:F3}; persist-publish-seconds={persistenceTime.TotalSeconds:F3}; ranking-seconds={rankingTime.TotalSeconds:F3}"
                    );
                    using var process = Process.GetCurrentProcess();
                    Console.WriteLine(
                        $"Memory: managed-bytes-after-buffering={bufferedManagedBytes}; process-lifetime-peak-working-set-bytes={process.PeakWorkingSet64}"
                    );
                    var rootData = result
                        .Folders.Values.Single(f => f.Reference.RecordIndex == 5)
                        .SubtreeData;
                    var accounted =
                        rootData + result.Metadata.KnownData + result.Unattributed.KnownData;
                    if (observed != accounted)
                        throw new InvalidDataException(
                            "Staged aggregation differs from scanned DATA capacity."
                        );
                    Console.WriteLine(
                        $"Capacity-accounting: observed-logical-bytes={observed.LogicalBytes}; observed-allocated-bytes={observed.AllocatedBytes}; conservation-match={observed == accounted}"
                    );
                    Console.WriteLine(
                        $"Folder-root-DATA: logical-bytes={rootData.LogicalBytes}; allocated-bytes={rootData.AllocatedBytes}"
                    );
                    Console.WriteLine(
                        $"Outside-folder-totals: metadata-known-allocated-bytes={result.Metadata.KnownData.AllocatedBytes}; unattributed-known-allocated-bytes={result.Unattributed.KnownData.AllocatedBytes}; unattributed-resolved={result.Unattributed.ResolvedCount}"
                    );
                    Console.WriteLine(
                        $"Name-count-diagnostics: header-mismatches={headerNameMismatches}; complete-name-sets={completeMismatchedNameSets}; policy=EnumeratedFileNames; disk-repairs=False"
                    );
                    foreach (var group in buffer.Diagnostics)
                    {
                        Console.WriteLine(
                            $"Unattributed: reason={group.Reason}; resolved={group.ResolvedCount}; unresolved={group.UnresolvedCount}; logical-bytes={group.KnownData.LogicalBytes}; allocated-bytes={group.KnownData.AllocatedBytes}"
                        );
                        foreach (var sample in group.Samples)
                            Console.WriteLine(
                                $"  Sample: record={sample.Reference.RecordIndex}; sequence={sample.Reference.SequenceNumber}; directory={sample.IsDirectory}; allocated-bytes={sample.Capacity?.AllocatedBytes.ToString() ?? "unknown"}; observed-name-count={sample.ObservedNameCount}; names={sample.Names}; visible-names={sample.VisibleNames}; dos-names={sample.DosNames}; first-parent={sample.FirstParent?.RecordIndex}; parent-sequence={sample.FirstParent?.SequenceNumber}; problem-parent={sample.ProblemParent?.RecordIndex}; problem-parent-sequence={sample.ProblemParent?.SequenceNumber}; hierarchy-issue={sample.HierarchyIssue}"
                            );
                    }
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
