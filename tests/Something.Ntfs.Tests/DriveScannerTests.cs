using System.Collections.Immutable;
using Something.Application;
using Something.Application.Browsing;
using Something.Application.History;
using Something.Application.Memory;
using Something.Application.Repositories;
using Something.Application.Scanning;
using Something.Statistics;

namespace Something.Ntfs.Tests;

/// <summary>How a scan reaches the history or a temporary database, without any database.</summary>
public sealed class DriveScannerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ADemoGoesToTheHistoryWhenOneIsGiven()
    {
        // Arrange
        var publisher = new FakePublisher();
        var temporary = new FakeTemporary();
        var scanner = new DriveScanner(temporary, History(publisher));
        var phases = new List<ScanPhase>();

        // Act
        await using var session = await scanner.CreateDemoAsync(
            new Progress<ScanProgress>(value => phases.Add(value.Phase)),
            Token
        );
        await Task.Yield();

        // Assert
        var snapshot = Assert.Single(publisher.Snapshots);
        Assert.Equal(0, temporary.Published);
        Assert.Equal(("Demo", "demo:Demo"), (publisher.Drive, snapshot.Entry.VolumeKey));
        Assert.True(session.IsDemo);
        Assert.Equal(snapshot.Entry.Id, session.ScanId);
        Assert.Equal(snapshot.Entry.Id, session.History!.Id);
        var timing = Assert.IsType<ScanTimingSnapshot>(snapshot.SavedTimings);
        Assert.Equal(timing, session.Timings);
        Assert.Equal(timing.TotalElapsed, session.History.Elapsed);
        Assert.Contains(ScanPhase.SavingHistory, phases);
        Assert.DoesNotContain(ScanPhase.Saving, phases);
        Assert.Equal(0, snapshot.Disposals);
    }

    [Fact]
    public async Task ADemoWithoutHistoryUsesATemporaryDatabase()
    {
        // Arrange
        var temporary = new FakeTemporary();
        var scanner = new DriveScanner(temporary);

        // Act
        var session = await scanner.CreateDemoAsync(null, Token, "DemoZ");
        var snapshot = Assert.Single(temporary.Snapshots);
        await session.DisposeAsync();

        // Assert
        Assert.Equal("DemoZ", session.Drive);
        Assert.Null(session.History);
        Assert.True(session.IsDemo);
        Assert.NotNull(snapshot.SavedTimings);
        Assert.Equal(1, snapshot.Disposals);
        Assert.Equal(snapshot.Completed.Root, session.RootSample);
    }

    [Fact]
    public async Task TheSnapshotIsReleasedOnceWhenTheSessionIsDisposedTwice()
    {
        // Arrange
        var publisher = new FakePublisher();
        var scanner = new DriveScanner(new FakeTemporary(), History(publisher));
        var session = await scanner.CreateDemoAsync(null, Token);

        // Act
        await session.DisposeAsync();
        await session.DisposeAsync();

        // Assert
        Assert.Equal(1, Assert.Single(publisher.Snapshots).Disposals);
    }

    [Fact]
    public async Task APublicationFailureLeavesNoSessionAndNoSnapshot()
    {
        // Arrange
        var publisher = new FakePublisher { Failure = new IOException("disk full") };
        var temporary = new FakeTemporary { Failure = new IOException("no temp space") };

        // Act and assert
        await Assert.ThrowsAsync<IOException>(() =>
            new DriveScanner(new FakeTemporary(), History(publisher)).CreateDemoAsync(null, Token)
        );
        await Assert.ThrowsAsync<IOException>(() =>
            new DriveScanner(temporary).CreateDemoAsync(null, Token)
        );
        Assert.Empty(publisher.Snapshots);
        Assert.Empty(temporary.Snapshots);
    }

    [Fact]
    public async Task AFailureWhileRecordingTimingsReleasesTheSnapshot()
    {
        // Arrange
        var publisher = new FakePublisher { TimingFailure = new IOException("timings") };
        var temporary = new FakeTemporary { TimingFailure = new IOException("timings") };

        // Act and assert
        await Assert.ThrowsAsync<IOException>(() =>
            new DriveScanner(new FakeTemporary(), History(publisher)).CreateDemoAsync(null, Token)
        );
        await Assert.ThrowsAsync<IOException>(() =>
            new DriveScanner(temporary).CreateDemoAsync(null, Token)
        );
        Assert.Equal(1, Assert.Single(publisher.Snapshots).Disposals);
        Assert.Equal(1, Assert.Single(temporary.Snapshots).Disposals);
    }

    [Fact]
    public async Task ThePublisherChargesItsMemoryToTheScansReservation()
    {
        // Arrange
        var publisher = new FakePublisher();
        var temporary = new FakeTemporary();

        // Act
        await using var history = await new DriveScanner(
            new FakeTemporary(),
            History(publisher)
        ).CreateDemoAsync(null, Token);
        await using var plain = await new DriveScanner(temporary).CreateDemoAsync(null, Token);

        // Assert: the reservation is released once the scan is done.
        Assert.IsType<ScanMemoryBudget.Reservation>(publisher.Allowance);
        Assert.IsType<ScanMemoryBudget.Reservation>(temporary.Allowance);
        Assert.Throws<ObjectDisposedException>(() => publisher.Allowance!.Hold(1));
        Assert.Throws<ObjectDisposedException>(() => temporary.Allowance!.Hold(1));
    }

    [Fact]
    public async Task ACancelledDemoPublishesNothing()
    {
        // Arrange
        var publisher = new FakePublisher();
        var temporary = new FakeTemporary();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // Act and assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DriveScanner(temporary, History(publisher)).CreateDemoAsync(null, cancelled.Token)
        );
        Assert.Empty(publisher.Snapshots);
        Assert.Equal(0, temporary.Published);
    }

    [Fact]
    public async Task InvalidStagesAreRejectedBeforeAnythingIsPublished()
    {
        // Arrange
        var publisher = new FakePublisher();
        var scanner = new DriveScanner(new FakeTemporary(), History(publisher));

        // Act and assert
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            scanner.CreateDemoAsync(null, Token, stage: (DemoScanStage)99)
        );
        Assert.Empty(publisher.Snapshots);
    }

    private static HistoryService History(IScanPublisher publisher) =>
        new(new HistoryRepositories(null!, publisher, null!, null!, null!));

    private static ScanHistoryEntry EntryOf(CompletedScan scan, string drive, TimeSpan elapsed) =>
        new(
            Guid.NewGuid(),
            scan.Descriptor.VolumeKey,
            drive,
            DateTimeOffset.UtcNow,
            DateOnly.FromDateTime(DateTime.UtcNow),
            0,
            elapsed
        );

    private sealed class FakePublisher : IScanPublisher
    {
        public List<FakeSnapshot> Snapshots { get; } = [];
        public IOException? Failure { get; init; }
        public IOException? TimingFailure { get; init; }
        public string? Drive { get; private set; }
        public IMemoryAllowance? Allowance { get; private set; }

        public IHistorySnapshot Publish(
            CompletedScan scan,
            DateTimeOffset endedUtc,
            string drive,
            TimeSpan elapsed,
            IMemoryAllowance allowance,
            Action<ScanWorkProgress>? progress,
            CancellationToken token
        )
        {
            token.ThrowIfCancellationRequested();
            if (Failure is not null)
                throw Failure;
            (Drive, Allowance) = (drive, allowance);
            var snapshot = new FakeSnapshot(scan, EntryOf(scan, drive, elapsed), TimingFailure);
            Snapshots.Add(snapshot);
            return snapshot;
        }
    }

    private sealed class FakeTemporary : ITemporaryScanRepository
    {
        public List<FakeSnapshot> Snapshots { get; } = [];
        public IOException? Failure { get; init; }
        public IOException? TimingFailure { get; init; }
        public IMemoryAllowance? Allowance { get; private set; }
        public int Published => Snapshots.Count;

        public IScanSnapshot Publish(
            CompletedScan scan,
            DateTimeOffset endedUtc,
            IMemoryAllowance allowance,
            Action<ScanWorkProgress>? progress,
            CancellationToken token
        )
        {
            token.ThrowIfCancellationRequested();
            if (Failure is not null)
                throw Failure;
            Allowance = allowance;
            var snapshot = new FakeSnapshot(
                scan,
                EntryOf(scan, "temporary", TimeSpan.Zero),
                TimingFailure
            );
            Snapshots.Add(snapshot);
            return snapshot;
        }
    }

    /// <summary>A published scan that remembers what was recorded and released.</summary>
    private sealed class FakeSnapshot(
        CompletedScan scan,
        ScanHistoryEntry entry,
        IOException? timingFailure
    ) : IHistorySnapshot
    {
        public CompletedScan Completed => scan;
        public ScanHistoryEntry Entry => entry;
        public ScanTimingSnapshot? SavedTimings { get; private set; }
        public int Disposals { get; private set; }
        public Guid ScanId => entry.Id;

        public ScanSnapshotDescription Describe() =>
            new(scan.Descriptor.VolumeKey, scan.Descriptor.VolumeSerial, entry.CompletedUtc);

        public (FolderCapacitySample Root, ulong MetadataUnresolved) ReadSummary(
            CancellationToken token
        ) => (scan.Root, scan.Aggregation.Metadata.UnresolvedCount);

        public ScanTimingSnapshot? ReadTimings(CancellationToken token) => SavedTimings;

        public void SaveTimings(ScanTimingSnapshot timing, CancellationToken token)
        {
            if (timingFailure is not null)
                throw timingFailure;
            SavedTimings = timing;
        }

        public void Dispose() => Disposals++;

        public FileReference? ReadParentFolder(
            FileReference folder,
            string visibleName,
            CancellationToken token
        ) => throw new NotSupportedException();

        public FolderEntryPage ReadFolderEntries(
            FileReference parent,
            int page,
            bool directoriesOnly,
            FolderEntrySort sort,
            bool includeTotalCount,
            bool descending,
            CancellationToken token
        ) => throw new NotSupportedException();

        public ImmutableArray<FolderEntry> ReadAllFolderEntries(
            FileReference parent,
            CancellationToken token
        ) => throw new NotSupportedException();

        public ImmutableArray<RankedFolder> ReadFolderAnalysis(
            FolderRankingFilter filter,
            FolderEntrySort sort,
            CancellationToken token
        ) => throw new NotSupportedException();

        public IEnumerable<FolderVisibilityObservation> ReadVisibilityFolders(
            CancellationToken token
        ) => throw new NotSupportedException();

        public IEnumerable<FileCapacityObservation> ReadFiles(CancellationToken token) =>
            throw new NotSupportedException();

        public IEnumerable<AnalysisFileName> ReadAnalysisFiles(CancellationToken token) =>
            throw new NotSupportedException();
    }
}
