using System.Diagnostics;
using System.Globalization;
using Something.Application.Browsing;
using Something.Application.Memory;
using Something.Application.Scanning;
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
        CancellationToken token,
        FolderRankingFilter? filter = null,
        DiagnosticDatabasePreference databasePreference = DiagnosticDatabasePreference.Automatic
    )
    {
        if (databasePreference == DiagnosticDatabasePreference.Automatic)
            databasePreference = DiagnosticDatabasePreference.File;
        var watch = Stopwatch.StartNew();
        long candidates = 0;
        string phase = "scan-and-buffer";
        using var heartbeatCancellation = new CancellationTokenSource();
        Console.Error.WriteLine(
            $"Scanning for top 5 folders; progress every 5s; Ctrl+C cancels. DuckDB preference={databasePreference}."
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
            // Source acquisition and DuckDB calls are synchronous; keep them off the heartbeat thread.
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
                    var storageTraits = new Dictionary<FileReference, FileStorageTraits>();
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
                        {
                            var traits = FolderAccountingDiagnostics.Classify(file);
                            if (traits != FileStorageTraits.None)
                                storageTraits.Add(file.Reference, traits);
                            foreach (var stream in file.DataStreams)
                                observed += new CapacityAmount(
                                    checked((UInt128)stream.LogicalBytes),
                                    checked((UInt128)stream.AllocatedBytes)
                                );
                        }
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
                    var completed = buffer.Complete(token);
                    var result = completed.Aggregation;
                    var completionTime = Stopwatch.GetElapsedTime(completionStarted);
                    ulong estimate = completed.EstimateDatabaseBytes(token);
                    var memory = OperatingSystem.IsWindows()
                        ? WindowsMemoryAvailability.TryRead()
                        : null;
                    var selection = DiagnosticDatabaseSelector.SelectDuckDb(
                        estimate,
                        memory is null
                            ? null
                            : new(
                                memory.TotalPhysicalBytes,
                                memory.AvailablePhysicalBytes,
                                memory.AvailableCommitBytes,
                                memory.AvailableVirtualBytes
                            ),
                        databasePreference
                    );
                    bool memoryDatabase = selection.UseMemory;
                    Console.Error.WriteLine(
                        $"Database-selection: memory={memoryDatabase}; reason={selection.Reason}; estimated-db-bytes={estimate}; required-additional-bytes={selection.RequiredAdditionalBytes}; reserve-bytes={selection.ReserveBytes}; duckdb-memory-limit-bytes={selection.FileCacheBytes}; available-physical-bytes={selection.Available?.PhysicalBytes.ToString() ?? "unknown"}; available-commit-bytes={selection.Available?.CommitBytes.ToString() ?? "unknown"}; available-virtual-bytes={selection.Available?.VirtualBytes.ToString() ?? "unknown"}"
                    );
                    Volatile.Write(ref phase, "persist-and-publish");
                    long persistenceStarted = Stopwatch.GetTimestamp();
                    using var temporary = TemporaryScanDatabase.Create(
                        token,
                        inMemory: memoryDatabase,
                        boundedMemory: !memoryDatabase,
                        fileCacheBytes: memoryDatabase ? null : selection.FileCacheBytes
                    );
                    var store = temporary.Store;
                    var id = store.PublishBufferedScan(completed, DateTimeOffset.UtcNow, token);
                    var persistenceTime = Stopwatch.GetElapsedTime(persistenceStarted);
                    var exportTime = TimeSpan.Zero;
                    long snapshotBytes = 0;
                    if (memoryDatabase)
                    {
                        Volatile.Write(ref phase, "export-database");
                        long exportStarted = Stopwatch.GetTimestamp();
                        snapshotBytes = temporary.ExportSnapshot(token);
                        exportTime = Stopwatch.GetElapsedTime(exportStarted);
                    }
                    Volatile.Write(ref phase, "rank-folders");
                    long rankingStarted = Stopwatch.GetTimestamp();
                    var top = store.ReadLargestFolders(
                        id,
                        cancellationToken: token,
                        filter: filter
                    );
                    var rankingTime = Stopwatch.GetElapsedTime(rankingStarted);
                    Volatile.Write(ref phase, "diagnose-accounting");
                    long diagnosticStarted = Stopwatch.GetTimestamp();
                    var accounting = top.ToDictionary(
                        item => item.Sample.Reference,
                        item =>
                            completed.ReadAccountingDiagnostics(
                                item.Sample.Reference,
                                storageTraits,
                                token
                            )
                    );
                    foreach (var item in top)
                        if (
                            accounting[item.Sample.Reference].AssignedData
                                != item.Sample.SubtreeData
                            || accounting[item.Sample.Reference].PerVisibleNameUnnamedData
                                != item.Data
                        )
                            throw new InvalidDataException(
                                "Folder accounting diagnostics differ from the published ranking."
                            );
                    var diagnosticTime = Stopwatch.GetElapsedTime(diagnosticStarted);
                    bool incomplete =
                        result.HasUnknownAttribution
                        || result.Unattributed.UnresolvedCount != 0
                        || result.Folders.Values.Any(f =>
                            f.SubtreeUnresolvedCount != 0 || f.SubtreeUnobservedDirectoryCount != 0
                        );
                    Console.WriteLine(
                        $"Top {top.Length} folders by known allocated unnamed DATA per visible file name in subtree (root excluded):"
                    );
                    Console.WriteLine(
                        "Accounting-policy: folders=PerVisibleNameUnnamedData; volume=SingleCountAllData; dos-aliases=Excluded; reparse-targets=NotFollowed"
                    );
                    Console.WriteLine(
                        $"Ranking-filter: depth={filter?.Depth?.ToString(CultureInfo.InvariantCulture) ?? "any"}; leaf-only={filter?.LeafOnly ?? false}; root-depth=0; subtree-included=True"
                    );
                    Console.WriteLine(
                        $"Database-mode: memory={memoryDatabase}; snapshot-bytes={snapshotBytes}; retained-history=False"
                    );
                    int rank = 0;
                    foreach (var item in top)
                    {
                        token.ThrowIfCancellationRequested();
                        string size = (
                            (double)item.Data.AllocatedBytes / (1024 * 1024 * 1024)
                        ).ToString("F3", CultureInfo.InvariantCulture);
                        string path = string.Concat(
                            item.RelativePath.Select(c =>
                                char.IsControl(c) || char.IsSurrogate(c)
                                    ? $"\\u{(int)c:X4}"
                                    : c.ToString()
                            )
                        );
                        Console.WriteLine(
                            $"{++rank}. {drive}\\{path} | {size} GiB | allocated-bytes={item.Data.AllocatedBytes}; logical-bytes={item.Data.LogicalBytes}; subtree-unresolved={item.Sample.Entries!.SubtreeUnresolvedCount}; file-names={item.Sample.Entries.SubtreeFileCount}"
                        );
                        Console.WriteLine(
                            $"  Single-count-DATA: logical-bytes={item.Sample.SubtreeData.LogicalBytes}; allocated-bytes={item.Sample.SubtreeData.AllocatedBytes}"
                        );
                        PrintAccounting(accounting[item.Sample.Reference]);
                    }
                    Console.WriteLine(
                        $"Elapsed={watch.Elapsed.TotalSeconds:F3}s; incomplete={incomplete}; metadata-unresolved={result.Metadata.UnresolvedCount}; unattributed-unresolved={result.Unattributed.UnresolvedCount}"
                    );
                    Console.WriteLine(
                        $"Timing: scan-and-buffer-seconds={scanTime.TotalSeconds:F3}; aggregate-buffer-seconds={completionTime.TotalSeconds:F3}; persist-publish-seconds={persistenceTime.TotalSeconds:F3}; db-snapshot-seconds={exportTime.TotalSeconds:F3}; ranking-seconds={rankingTime.TotalSeconds:F3}; accounting-diagnostics-seconds={diagnosticTime.TotalSeconds:F3}"
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
                        $"Folder-root-single-count-DATA: logical-bytes={rootData.LogicalBytes}; allocated-bytes={rootData.AllocatedBytes}"
                    );
                    Console.WriteLine(
                        $"Outside-folder-totals: metadata-known-allocated-bytes={result.Metadata.KnownData.AllocatedBytes}; unattributed-known-allocated-bytes={result.Unattributed.KnownData.AllocatedBytes}; unattributed-resolved={result.Unattributed.ResolvedCount}"
                    );
                    Console.WriteLine(
                        $"Name-count-diagnostics: header-mismatches={headerNameMismatches}; complete-name-sets={completeMismatchedNameSets}; policy=EnumeratedFileNames; disk-repairs=False"
                    );
                    foreach (var group in completed.Diagnostics)
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

    private static void PrintAccounting(FolderAccountingSummary summary)
    {
        Console.WriteLine(
            $"  Name-view-unnamed-DATA: logical-bytes={summary.PerVisibleNameUnnamedData.LogicalBytes}; allocated-bytes={summary.PerVisibleNameUnnamedData.AllocatedBytes}; visible-files={summary.VisibleFiles}; visible-names={summary.VisibleNames}; descendant-directories={summary.DescendantDirectories}; incomplete-name-sets={summary.IncompleteNameSets}; unattributed-visible-files={summary.UnattributedVisibleFiles}; unresolved-visible-files={summary.UnresolvedVisibleFiles}"
        );
        Console.WriteLine(
            $"  Hard-links: multiple-name-files-in-subtree={summary.MultipleNameFiles}; dos-aliases={summary.DosAliases}; duplicate-extra-logical-bytes={summary.DuplicateNameExtraUnnamedData.LogicalBytes}; duplicate-extra-allocated-bytes={summary.DuplicateNameExtraUnnamedData.AllocatedBytes}; cross-boundary-logical-bytes={summary.CrossBoundaryUnnamedData.LogicalBytes}; cross-boundary-allocated-bytes={summary.CrossBoundaryUnnamedData.AllocatedBytes}"
        );
        Console.WriteLine(
            $"  Assigned-other-DATA: named-logical-bytes={summary.AssignedNamedStreams.LogicalBytes}; named-allocated-bytes={summary.AssignedNamedStreams.AllocatedBytes}; directory-logical-bytes={summary.AssignedDirectoryData.LogicalBytes}; directory-allocated-bytes={summary.AssignedDirectoryData.AllocatedBytes}; reparse-directories={summary.ReparseDirectories}"
        );
        foreach (var (kind, bucket) in summary.SpecialFiles.OrderBy(p => p.Key))
            Console.WriteLine(
                $"  Special-file-DATA: kind={kind}; files={bucket.ResolvedCount}; unresolved={bucket.UnresolvedCount}; logical-bytes={bucket.KnownData.LogicalBytes}; allocated-bytes={bucket.KnownData.AllocatedBytes}"
            );
        Console.WriteLine(
            "  Name view counts unnamed DATA per non-DOS name; reparse targets are not followed. Special-file groups overlap and include all DATA of each selected file once; they must not be added together."
        );
    }
}
