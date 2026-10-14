using System.Diagnostics;
using Something.Ntfs;
using Something.Statistics;
using Something.Storage;
using Something.Windows;

namespace Something.Application;

public enum ScanPhase
{
    WaitingForMemory,
    Reading,
    Aggregating,
    Saving,
    WritingSnapshot,
    SavingHistory,
    Completed,
}

public sealed record ScanProgress(
    ScanPhase Phase,
    ulong Candidates,
    double? Fraction,
    TimeSpan Elapsed
)
{
    public ScanWorkProgress? Work { get; init; }
    public bool SavesHistory { get; init; }
    public ScanStep Step => ScanProgressSteps.Locate(Phase, Work?.Stage, SavesHistory);
}

/// <summary>Worker-owned acquisition and storage orchestration. Never runs acquisition on the UI thread.</summary>
public sealed class DriveScanner(ScanHistoryArchive? history = null)
{
    private readonly ScanMemoryBudget memoryBudget = new(ReadAvailable);

    private static AvailableMemory? ReadAvailable()
    {
        var memory = OperatingSystem.IsWindows() ? WindowsMemoryAvailability.TryRead() : null;
        return memory is null
            ? null
            : new(
                memory.TotalPhysicalBytes,
                memory.AvailablePhysicalBytes,
                memory.AvailableCommitBytes,
                memory.AvailableVirtualBytes
            );
    }

    public Task<ScanSession> ScanAsync(
        string drive,
        IProgress<ScanProgress>? progress,
        CancellationToken token
    ) =>
        Task.Run(
            async () =>
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException();
                string volumeKey = history is null ? "" : WindowsVolumeIdentity.ReadKey(drive);
                using var source = WindowsMftRecordSource.Open(drive);
                if (history is not null && volumeKey != WindowsVolumeIdentity.ReadKey(drive))
                    throw new IOException("Volume mount changed while opening scan.");
                var watch = Stopwatch.StartNew();
                var reports = progress is null
                    ? null
                    : new HistoryProgress(progress, history is not null);
                reports?.Report(new(ScanPhase.WaitingForMemory, 0, null, watch.Elapsed));
                // Observed slots include unused records. 1 KiB per slot is a conservative
                // initial buffer/aggregation heuristic, independent of file content size.
                UInt128 estimate = (UInt128)source.Volume.ObservedRecordCount * 1024;
                using var reservation = await memoryBudget.ReserveAsync(
                    (ulong)UInt128.Min(ulong.MaxValue, UInt128.Max(64UL << 20, estimate)),
                    token
                );
                var scope = Guid.NewGuid();
                var buffer = new BufferedScan(
                    new(
                        history is null
                            ? $"diagnostic:{scope:N}"
                            : $"{volumeKey}:{source.Volume.SerialNumber:X16}",
                        scope,
                        source.Volume.SerialNumber,
                        new(0, source.Volume.ObservedRecordCount),
                        DateTimeOffset.UtcNow
                    )
                );
                ulong candidates = 0;
                TimeSpan lastReport = TimeSpan.Zero;
                reports?.Report(new(ScanPhase.Reading, 0, 0, watch.Elapsed));
                await foreach (
                    var entry in new LiveMftScanner(
                        source.Volume.BytesPerCluster,
                        source.Volume.TotalClusters
                    ).ScanAsync(source, buffer.Descriptor.Range, token)
                )
                {
                    buffer.Add(entry, token);
                    candidates++;
                    if (watch.Elapsed - lastReport < TimeSpan.FromMilliseconds(250))
                        continue;
                    lastReport = watch.Elapsed;
                    reports?.Report(
                        new(
                            ScanPhase.Reading,
                            candidates,
                            (double)(source.Volume.ObservedRecordCount - entry.RecordIndex)
                                / source.Volume.ObservedRecordCount,
                            watch.Elapsed
                        )
                    );
                }
                return Publish(
                    buffer,
                    drive,
                    false,
                    candidates,
                    watch,
                    reports,
                    token,
                    reservation
                );
            },
            token
        );

    /// <summary>Owned synthetic observations for UI review; no drive, directory or file is inspected.</summary>
    public Task<ScanSession> CreateDemoAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken token,
        string drive = "Demo"
    ) =>
        Task.Run(
            async () =>
            {
                var watch = Stopwatch.StartNew();
                using var reservation = await memoryBudget.ReserveAsync(64UL << 20, token);
                var buffer = new BufferedScan(
                    new($"demo:{drive}", Guid.NewGuid(), 1, new(0, 1000), DateTimeOffset.UtcNow)
                );
                var root = FileReference.Create(5, 1);
                void Add(
                    ulong index,
                    string name,
                    bool directory,
                    FileReference parent,
                    long logical,
                    long allocated
                )
                {
                    var file = new FileCapacityObservation(
                        FileReference.Create(index, 1),
                        directory,
                        CapacityDomain.Ordinary,
                        CapacityResolution.Resolved,
                        [new(parent, name, FileNameNamespace.Win32, 0)],
                        directory ? [] : [new("", logical, allocated)],
                        true
                    );
                    buffer.Add(
                        new StagedObservation(
                            index,
                            MftScanEntryKind.ResolvedFile,
                            LiveFileReadStatus.ObservedUnchanged,
                            file
                        ),
                        token
                    );
                }
                Add(5, "Demo", true, root, 0, 0);
                Add(20, "Projects", true, root, 0, 0);
                Add(21, "Media", true, root, 0, 0);
                Add(22, "Archive", true, FileReference.Create(20, 1), 0, 0);
                Add(100, "notes.md", false, root, 2450, 4096);
                Add(
                    101,
                    "capture.mp4",
                    false,
                    FileReference.Create(21, 1),
                    4_831_838_208,
                    4_831_838_208
                );
                Add(
                    102,
                    "source.zip",
                    false,
                    FileReference.Create(22, 1),
                    1_610_612_736,
                    1_610_612_736
                );
                Add(103, "preview.png", false, FileReference.Create(20, 1), 12_581_034, 12_582_912);
                return Publish(buffer, drive, true, 8, watch, progress, token, reservation);
            },
            token
        );

    private sealed class HistoryProgress(IProgress<ScanProgress> target, bool savesHistory)
        : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) =>
            target.Report(value with { SavesHistory = savesHistory });
    }

    private ScanSession Publish(
        BufferedScan buffer,
        string drive,
        bool demo,
        ulong candidates,
        Stopwatch watch,
        IProgress<ScanProgress>? progress,
        CancellationToken token,
        ScanMemoryBudget.Reservation reservation
    )
    {
        progress?.Report(new(ScanPhase.Aggregating, candidates, null, watch.Elapsed));
        var aggregation = buffer.Complete(
            token,
            progress is null ? null : value => ReportWork(ScanPhase.Aggregating, value)
        );
        var selection = reservation.SelectDuckDatabase(buffer.EstimateDatabaseBytes(token));
        if (history is not null && !demo)
        {
            progress?.Report(new(ScanPhase.SavingHistory, candidates, null, watch.Elapsed));
            var entry = history.Publish(
                buffer,
                DateTimeOffset.UtcNow,
                drive,
                watch.Elapsed,
                selection.FileCacheBytes,
                token,
                progress is null ? null : value => ReportWork(ScanPhase.SavingHistory, value),
                reservation.TryGrowDuckDatabase,
                reservation.AccountDuckDatabaseLimit
            );
            var session = ScanSession.FromHistory(history.Open(entry.Id, token), drive, token);
            progress?.Report(new(ScanPhase.Completed, candidates, 1, watch.Elapsed));
            return session;
        }
        progress?.Report(new(ScanPhase.Saving, candidates, null, watch.Elapsed));
        var database = TemporaryScanDatabase.Create(
            token,
            selection.UseMemory,
            !selection.UseMemory,
            selection.UseMemory ? null : selection.FileCacheBytes
        );
        try
        {
            var id = database.Store.PublishBufferedScan(
                buffer,
                DateTimeOffset.UtcNow,
                token,
                progress is null ? null : value => ReportWork(ScanPhase.Saving, value),
                reservation.TryGrowDuckDatabase,
                reservation.AccountDuckDatabaseLimit
            );
            if (selection.UseMemory)
            {
                progress?.Report(new(ScanPhase.WritingSnapshot, candidates, null, watch.Elapsed));
                database.ExportSnapshot(token);
            }
            var root = aggregation.Folders.Keys.Single(r => r.RecordIndex == 5);
            progress?.Report(new(ScanPhase.Completed, candidates, 1, watch.Elapsed));
            return new(
                database,
                id,
                drive,
                root,
                aggregation.Folders[root],
                aggregation.Metadata.UnresolvedCount,
                selection,
                demo,
                watch.Elapsed
            );
        }
        catch
        {
            database.Dispose();
            throw;
        }

        void ReportWork(ScanPhase phase, ScanWorkProgress value) =>
            progress?.Report(
                new(phase, candidates, value.Fraction, watch.Elapsed) { Work = value }
            );
    }
}
