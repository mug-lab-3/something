using Something.Application;
using Something.Application.History;
using Something.Application.Scanning;
using Something.Storage;

namespace Something.Ntfs.Tests;

public sealed class ScanTimingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void CollectorConservesWholeDurationAndSeparatesRetriedSteps()
    {
        var collector = new ScanTimingCollector(true);
        Report(ScanPhase.Reading, 0);
        Report(ScanPhase.Aggregating, 5, ScanWorkStage.CollectingFolders);
        Report(ScanPhase.Aggregating, 6, ScanWorkStage.CollectingFolders);
        Report(ScanPhase.Aggregating, 7, ScanWorkStage.BuildingHierarchy);
        Report(ScanPhase.Aggregating, 8, ScanWorkStage.AggregatingFiles);
        Report(ScanPhase.SavingHistory, 12, ScanWorkStage.WritingFiles);
        Report(ScanPhase.SavingHistory, 15, ScanWorkStage.BuildingFolderList);
        Report(ScanPhase.SavingHistory, 18, ScanWorkStage.WritingFiles, 1);
        Report(ScanPhase.SavingHistory, 20, ScanWorkStage.MaintainingHistory);

        var result = collector.Complete(TimeSpan.FromSeconds(22));

        Assert.Equal(14, result.TotalSteps);
        Assert.Equal(TimeSpan.FromSeconds(22), result.TotalElapsed);
        Assert.Equal(result.TotalElapsed.Ticks, result.Steps.Sum(s => s.Elapsed.Ticks));
        Assert.Equal([1, 2, 3, 4, 7, 12, 7, 14], result.Steps.Select(s => s.Step));
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            Assert.Single(result.Steps, s => s.Attempt == 1).Elapsed
        );
        Assert.Equal(TimeSpan.FromSeconds(2), result.Steps[1].Elapsed);
        Assert.Throws<InvalidOperationException>(() =>
            collector.Complete(TimeSpan.FromSeconds(22))
        );

        void Report(ScanPhase phase, int seconds, ScanWorkStage? work = null, int attempt = 0) =>
            collector.Report(
                new(phase, 0, null, TimeSpan.FromSeconds(seconds))
                {
                    Work = work is { } stage
                        ? new ScanWorkProgress(stage, 0, null) { RetryAttempt = attempt }
                        : null,
                }
            );
    }

    [Fact]
    public void CollectorRejectsBackwardClockInsteadOfRecordingNegativeIntervals()
    {
        var collector = new ScanTimingCollector(false);
        collector.Report(new(ScanPhase.Reading, 0, null, TimeSpan.FromSeconds(3)));

        Assert.Throws<InvalidOperationException>(() =>
            collector.Report(new(ScanPhase.Reading, 0, null, TimeSpan.FromSeconds(2)))
        );
        Assert.Throws<InvalidOperationException>(() => collector.Complete(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task TimingDiagnosticsSurviveHistoryReopenAndAreDeletedWithGeneration()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "something-timing-test-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            var archive = new ScanHistoryArchive(directory);
            await using var session = await new DriveScanner(
                StorageRepositories.TemporaryScans,
                TestHistory.For(archive)
            ).CreateDemoAsync(null, Token);
            var original = Assert.IsType<ScanTimingSnapshot>(session.Timings);
            var entry = session.History!;

            await using (
                var restored = await TestHistory.For(archive).OpenAsync(entry.Id, "Demo", Token)
            )
            {
                Assert.Equal(original.TotalElapsed, restored.Elapsed);
                Assert.Equal(
                    original.Steps,
                    Assert.IsType<ScanTimingSnapshot>(restored.Timings).Steps
                );
            }
            using var store = ScanStore.OpenSharedHistoryReader(
                Path.Combine(directory, ScanHistoryArchive.DatabaseFileName),
                Token
            );
            var replacement = new ScanTimingSnapshot(
                13,
                TimeSpan.FromSeconds(10),
                [
                    new(1, 0, null, TimeSpan.FromSeconds(4)),
                    new(13, 0, ScanWorkStage.Committing, TimeSpan.FromSeconds(6)),
                ]
            );
            archive.SaveTimings(entry.Id, replacement, Token);
            Assert.Equal(
                replacement.TotalElapsed,
                Assert.Single(archive.List(entry.VolumeKey, Token)).Elapsed
            );
            Assert.Equal(
                replacement.Steps,
                Assert.IsType<ScanTimingSnapshot>(store.ReadTimings(entry.Id, Token)).Steps
            );

            store.DeleteHistoryGeneration(entry.Id);
            Assert.Null(store.ReadTimings(entry.Id, Token));
            Assert.Empty(store.HistoryContext.StepTimings);
        }
        finally
        {
            // Only files created by this test, after all native handles have been disposed.
            File.Delete(Path.Combine(directory, ScanHistoryArchive.DatabaseFileName));
            File.Delete(Path.Combine(directory, ScanHistoryArchive.DatabaseFileName + ".wal"));
            foreach (
                var lease in Directory.EnumerateFiles(Path.Combine(directory, "leases"), "*.lock")
            )
                File.Delete(lease);
            Directory.Delete(Path.Combine(directory, "leases"));
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task InvalidOrCancelledTimingUpdatePreservesPreviousDiagnostics()
    {
        await using var session = await new DriveScanner(
            StorageRepositories.TemporaryScans
        ).CreateDemoAsync(null, Token);
        var original = Assert.IsType<ScanTimingSnapshot>(session.Timings);
        // The demo's owned store is exercised through a history writer, so use a dedicated file store.
        using var database = TemporaryScanDatabase.Create(Token);
        var buffer = new BufferedScan(
            new("demo:timing", Guid.NewGuid(), 1, new(0, 100), DateTimeOffset.UtcNow)
        );
        var root = Something.Ntfs.FileReference.Create(5, 1);
        buffer.Add(
            new StagedObservation(
                5,
                Something.Ntfs.MftScanEntryKind.ResolvedFile,
                Something.Ntfs.LiveFileReadStatus.ObservedUnchanged,
                new(
                    root,
                    true,
                    Something.Statistics.CapacityDomain.Ordinary,
                    Something.Statistics.CapacityResolution.Resolved,
                    [new(root, "root", Something.Ntfs.FileNameNamespace.Win32, 0)],
                    [],
                    true
                )
            ),
            Token
        );
        buffer.Complete(Token);
        var id = database.Store.PublishBufferedScan(
            buffer.Complete(TestContext.Current.CancellationToken),
            DateTimeOffset.UtcNow,
            Token
        );
        database.Store.SaveTimings(id, original, Token);
        Assert.Throws<ArgumentException>(() =>
            database.Store.SaveTimings(
                id,
                original with
                {
                    TotalElapsed = original.TotalElapsed + TimeSpan.FromSeconds(1),
                },
                Token
            )
        );
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            database.Store.SaveTimings(id, original, cancellation.Token)
        );
        Assert.Equal(
            original.Steps,
            Assert.IsType<ScanTimingSnapshot>(database.Store.ReadTimings(id, Token)).Steps
        );
    }
}
