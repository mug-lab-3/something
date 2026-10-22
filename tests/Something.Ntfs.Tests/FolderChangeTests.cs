using System.Numerics;
using Microsoft.EntityFrameworkCore;
using Something.Application;
using Something.Application.Comparison;
using Something.Application.History;
using Something.Application.Scanning;
using Something.Statistics;
using Something.Storage;

namespace Something.Ntfs.Tests;

public sealed class FolderChangeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record Folder(
        ulong Index,
        string Name,
        UInt128 Bytes,
        ushort Sequence = 1,
        bool Unresolved = false
    );

    [Theory]
    [InlineData(FolderChangePeriod.Last24Hours, 1)]
    [InlineData(FolderChangePeriod.Last7Days, 7)]
    [InlineData(FolderChangePeriod.Last30Days, 30)]
    [InlineData(FolderChangePeriod.Last365Days, 365)]
    public async Task PeriodSelectsRetainedEndpointsAndRanksSignedAllocatedChanges(
        FolderChangePeriod period,
        int days
    )
    {
        using var fixture = new Fixture();
        var baseline = fixture.Save(
            Now.AddDays(-days).AddHours(-2),
            [new(20, "growing", 100), new(30, "shrinking", 500), new(40, "stable", 10)]
        );
        var latest = fixture.Save(
            Now.AddMinutes(-1),
            [new(20, "renamed", 400), new(30, "shrinking", 50), new(40, "stable", 10)]
        );
        fixture.Save(Now.AddMinutes(1), [new(20, "future", 9999)]);

        var report = await new FolderHistoryService(
            TestHistory.For(fixture.Archive)
        ).ReadChangesAsync("volume:A", period, Token, Now);

        Assert.Equal(FolderComparisonAvailability.Available, report.Availability);
        Assert.Equal(Now.AddDays(-days), report.RequestedBaselineUtc);
        Assert.Equal(baseline.Id, report.Baseline!.Id);
        Assert.Equal(latest.Id, report.Latest!.Id);
        Assert.Equal(2, report.ChangedFolders.Length);
        var growth = Assert.Single(report.Rank(FolderChangeRanking.Growth));
        Assert.Equal(new BigInteger(300), growth.AllocatedDelta);
        Assert.Equal("growing", growth.BeforePath);
        Assert.Equal("renamed", growth.AfterPath);
        Assert.Equal(
            new BigInteger(-450),
            Assert.Single(report.Rank(FolderChangeRanking.Shrinkage)).AllocatedDelta
        );
        Assert.Equal(
            new ulong[] { 30, 20 },
            report.Rank(FolderChangeRanking.Magnitude).Select(row => row.Reference.RecordIndex)
        );
        Assert.Single(Directory.GetFiles(fixture.Root, "*.duckdb"));
    }

    [Fact]
    public void AnalysisReturnsAllChangesWithoutTheTopTwentyLimit()
    {
        using var fixture = new Fixture();
        var before = fixture.Save(
            Now.AddHours(-1),
            [
                .. Enumerable
                    .Range(20, 50)
                    .Select(index => new Folder((ulong)index, $"folder-{index}", 1)),
            ]
        );
        var after = fixture.Save(
            Now,
            [
                .. Enumerable
                    .Range(20, 50)
                    .Select(index => new Folder((ulong)index, $"folder-{index}", (UInt128)index)),
            ]
        );
        var report = fixture.Archive.ReadFolderChanges("volume:A", before.Id, after.Id, Token);

        var all = report.Analyze(FolderChangeRanking.Growth);

        Assert.Equal(50, all.Length);
        Assert.Equal((ulong)69, all[0].Reference.RecordIndex);
        Assert.Equal((ulong)20, all[^1].Reference.RecordIndex);
        Assert.Equal(20, report.Rank(FolderChangeRanking.Growth).Length);
        Assert.Empty(report.Analyze(FolderChangeRanking.Shrinkage));
    }

    [Fact]
    public void MissingEndpointsDoNotCreateZeroChanges()
    {
        using var fixture = new Fixture();
        var noData = fixture.Archive.ReadFolderChanges(
            "volume:A",
            FolderChangePeriod.Last24Hours,
            Now,
            Token
        );
        Assert.Equal(FolderComparisonAvailability.NoLatestObservation, noData.Availability);
        fixture.Save(Now.AddMinutes(-1), [new(20, "folder", 100)]);
        var noBaseline = fixture.Archive.ReadFolderChanges(
            "volume:A",
            FolderChangePeriod.Last24Hours,
            Now,
            Token
        );
        Assert.Equal(FolderComparisonAvailability.NoBaseline, noBaseline.Availability);
        Assert.Empty(noBaseline.Folders);
        var stale = fixture.Archive.ReadFolderChanges(
            "volume:A",
            FolderChangePeriod.Last24Hours,
            Now.AddDays(2),
            Token
        );
        Assert.Equal(FolderComparisonAvailability.NoRecentObservation, stale.Availability);
        Assert.Empty(stale.Rank(FolderChangeRanking.Growth));
    }

    [Fact]
    public void ReusedReferencesMissingFoldersAndUnresolvedSizesAreNotRankedAsChanges()
    {
        using var fixture = new Fixture();
        fixture.Save(
            Now.AddDays(-2),
            [new(20, "old", 50), new(30, "missing", 100), new(40, "uncertain", 10)]
        );
        fixture.Save(
            Now,
            [
                new(20, "reused", 999, Sequence: 2),
                new(40, "uncertain", 10, Unresolved: true),
                new(50, "unseen", 200),
            ]
        );

        var report = fixture.Archive.ReadFolderChanges(
            "volume:A",
            FolderChangePeriod.Last24Hours,
            Now,
            Token
        );

        Assert.Equal(5, report.Folders.Length);
        Assert.All(report.Folders, row => Assert.Null(row.AllocatedDelta));
        Assert.Empty(report.ChangedFolders);
        Assert.Equal(5, report.UncertainFolders.Length);
        Assert.Equal(
            2,
            report.Folders.Count(row => row.Status == FolderChangeStatus.NotObservedBefore)
        );
        Assert.Equal(
            2,
            report.Folders.Count(row => row.Status == FolderChangeStatus.NotObservedAfter)
        );
        Assert.Single(report.Folders, row => row.Status == FolderChangeStatus.UncertainCapacity);
        Assert.Empty(report.Rank(FolderChangeRanking.Magnitude));
    }

    [Fact]
    public void DeltaSupportsFullUnsignedCapacityRange()
    {
        using var fixture = new Fixture();
        var baseline = fixture.Save(Now.AddDays(-2), [new(20, "folder", 100)]);
        fixture.Save(Now, [new(20, "folder", 0)]);
        using (
            var writer = ScanStore.OpenSharedHistoryWriter(
                Path.Combine(fixture.Root, ScanHistoryArchive.DatabaseFileName),
                null,
                Token
            )
        )
        {
            var db = Assert.IsType<SharedHistoryDbContext>(writer.HistoryContext);
            var version = db
                .SharedFolders.Single(row =>
                    row.Generation == SharedHistoryPublication.GenerationOf(db, baseline.Id)
                    && row.RecordIndex == 20
                )
                .SampleVersion;
            // States are keyless and untracked; rewrite the stored row directly.
            db.Samples.Where(row => row.Version == version && row.RecordIndex == 20)
                .ExecuteUpdate(row =>
                    row.SetProperty(r => r.EntrySubtreeAllocated, UInt128.MaxValue)
                );
        }

        var report = fixture.Archive.ReadFolderChanges(
            "volume:A",
            FolderChangePeriod.Last24Hours,
            Now,
            Token
        );

        Assert.Equal(
            -BigInteger.CreateChecked(UInt128.MaxValue),
            Assert.Single(report.Folders).AllocatedDelta
        );
        Assert.Single(report.Rank(FolderChangeRanking.Shrinkage));
    }

    [Fact]
    public void VolumeIsolationCancellationAndArgumentValidation()
    {
        using var fixture = new Fixture();
        fixture.Save(Now.AddDays(-2), [new(20, "folder", 100)], "volume:B");
        fixture.Save(Now, [new(20, "folder", 200)]);
        Assert.Equal(
            FolderComparisonAvailability.NoBaseline,
            fixture
                .Archive.ReadFolderChanges("volume:A", FolderChangePeriod.Last24Hours, Now, Token)
                .Availability
        );
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            fixture.Archive.ReadFolderChanges(
                "volume:A",
                FolderChangePeriod.Last24Hours,
                Now,
                cancelled.Token
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fixture.Archive.ReadFolderChanges("volume:A", (FolderChangePeriod)99, Now, Token)
        );
        var report = fixture.Archive.ReadFolderChanges(
            "volume:A",
            FolderChangePeriod.Last24Hours,
            Now,
            Token
        );
        Assert.Throws<ArgumentOutOfRangeException>(() => report.Rank((FolderChangeRanking)99));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            report.Rank(FolderChangeRanking.Growth, 0)
        );
    }

    [Fact]
    public void LegacyMetricIsNotSilentlySubstituted()
    {
        using var fixture = new Fixture();
        var old = fixture.Save(Now.AddDays(-2), [new(20, "folder", 100)]);
        fixture.Save(Now, [new(20, "folder", 200)]);
        using (
            var writer = ScanStore.OpenSharedHistoryWriter(
                Path.Combine(fixture.Root, ScanHistoryArchive.DatabaseFileName),
                null,
                Token
            )
        )
        {
            var db = Assert.IsType<SharedHistoryDbContext>(writer.HistoryContext);
            var version = db
                .SharedFolders.Single(row =>
                    row.Generation == SharedHistoryPublication.GenerationOf(db, old.Id)
                    && row.RecordIndex == 20
                )
                .SampleVersion;
            // States are keyless and untracked; rewrite the stored row directly.
            db.Samples.Where(row => row.Version == version && row.RecordIndex == 20)
                .ExecuteUpdate(row =>
                    row.SetProperty(r => r.EntrySubtreeAllocated, (UInt128?)null)
                        .SetProperty(r => r.EntrySubtreeLogical, (UInt128?)null)
                        .SetProperty(r => r.EntryDirectAllocated, (UInt128?)null)
                        .SetProperty(r => r.EntryDirectLogical, (UInt128?)null)
                        .SetProperty(r => r.EntryDirectFiles, (long?)null)
                        .SetProperty(r => r.EntrySubtreeFiles, (long?)null)
                        .SetProperty(r => r.EntryDirectUnresolved, (long?)null)
                        .SetProperty(r => r.EntrySubtreeUnresolved, (long?)null)
                );
        }
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Archive.ReadFolderChanges(
                "volume:A",
                FolderChangePeriod.Last24Hours,
                Now,
                Token
            )
        );
    }

    [Fact]
    public void FailedHistoryMetadataCommitRollsBackFactsAndLatestPointer()
    {
        using var fixture = new Fixture();
        var previous = fixture.Save(Now.AddMinutes(-1), [new(20, "folder", 100)]);
        using (
            var writer = ScanStore.OpenSharedHistoryWriter(
                Path.Combine(fixture.Root, ScanHistoryArchive.DatabaseFileName),
                null,
                Token
            )
        )
        {
            using var source = fixture.Archive.Open(previous.Id, Token);
            var buffer = new BufferedScan(source.Store.GetScan(previous.Id).Descriptor);
            foreach (var file in source.Store.ReadFiles(previous.Id, Token))
                buffer.Add(
                    new StagedObservation(
                        file.Reference.RecordIndex,
                        MftScanEntryKind.ResolvedFile,
                        LiveFileReadStatus.ObservedUnchanged,
                        file
                    ),
                    Token
                );
            buffer.Complete(Token);
            Assert.Throws<DbUpdateException>(() =>
                writer.PublishHistoryBuffer(
                    buffer.Complete(TestContext.Current.CancellationToken),
                    Now,
                    new()
                    {
                        VolumeKey = "volume:A",
                        Drive = "Demo",
                        CompletedUtcTicks = Now.UtcTicks,
                        LocalDay = -1,
                    },
                    Token
                )
            );
        }

        Assert.Equal(previous.Id, Assert.Single(fixture.Archive.List("volume:A", Token)).Id);
        using var stored = ScanStore.OpenSharedHistoryReader(
            Path.Combine(fixture.Root, ScanHistoryArchive.DatabaseFileName),
            Token
        );
        Assert.Equal(previous.Id, Assert.Single(stored.HistoryContext.Scans).Id);
        Assert.Equal(previous.Id, Assert.Single(stored.HistoryContext.Published).ScanId);
        Assert.All(
            stored.HistoryContext.Observations,
            row => Assert.Equal(previous.Id, row.ScanId)
        );
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData(FolderChangePeriod.Last24Hours)]
    [InlineData(FolderChangePeriod.Last7Days)]
    [InlineData(FolderChangePeriod.Last30Days)]
    [InlineData(FolderChangePeriod.Last365Days)]
    [InlineData(FolderChangePeriod.Today)]
    [InlineData(FolderChangePeriod.ThisWeek)]
    [InlineData(FolderChangePeriod.ThisMonth)]
    [InlineData(FolderChangePeriod.ThisYear)]
    public void PresetsCompareAvailableHistoryAndExposePartialCoverage(FolderChangePeriod period)
    {
        using var fixture = new Fixture();
        var first = fixture.Save(Now.AddMinutes(-5), [new(20, "folder", 100)]);
        var last = fixture.Save(Now.AddMinutes(-1), [new(20, "folder", 300)]);
        fixture.Save(Now.AddMinutes(1), [new(20, "folder", 999)]);

        var report = fixture.Archive.ReadFolderChanges("volume:A", period, Now, Token);

        Assert.Equal(FolderComparisonAvailability.Available, report.Availability);
        Assert.True(report.IsPartial);
        Assert.Equal(first.Id, report.Baseline!.Id);
        Assert.Equal(last.Id, report.Latest!.Id);
        Assert.True(report.RequestedBaselineUtc < first.CompletedUtc);
        Assert.Equal(new BigInteger(200), Assert.Single(report.ChangedFolders).AllocatedDelta);
        var selected = ComparisonSelection.Resolve(
            fixture.Archive.List("volume:A", Token),
            period,
            Now,
            TimeZoneInfo.Utc
        );
        Assert.Equal(selected.Before!.Id, report.Baseline.Id);
        Assert.Equal(selected.After!.Id, report.Latest.Id);
        Assert.Equal(selected.IsPartial, report.IsPartial);
    }

    [Fact]
    public async Task ExplicitScansCompareSignedSizesAndRejectWrongVolumeOrSameScan()
    {
        using var fixture = new Fixture();
        var before = fixture.Save(Now.AddMinutes(-20), [new(20, "folder", 100)]);
        var after = fixture.Save(Now.AddMinutes(-10), [new(20, "folder", 300)]);
        var other = fixture.Save(Now.AddMinutes(-5), [new(20, "folder", 999)], "volume:B");

        var report = await new FolderHistoryService(
            TestHistory.For(fixture.Archive)
        ).ReadChangesAsync("volume:A", before.Id, after.Id, Token);

        Assert.Equal(new BigInteger(200), Assert.Single(report.ChangedFolders).AllocatedDelta);
        Assert.Equal(new BigInteger(200), report.Root!.AllocatedDelta);
        Assert.Equal(before.Id, report.Baseline!.Id);
        Assert.Equal(after.Id, report.Latest!.Id);
        Assert.Throws<ArgumentException>(() =>
            fixture.Archive.ReadFolderChanges("volume:A", before.Id, before.Id, Token)
        );
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Archive.ReadFolderChanges("volume:A", before.Id, other.Id, Token)
        );
    }

    [Fact]
    public void ExplicitScansCompareInEitherOrder()
    {
        // Arrange
        using var fixture = new Fixture();
        var older = fixture.Save(
            Now.AddMinutes(-20),
            [new(20, "kept", 100), new(21, "removed", 50)]
        );
        var newer = fixture.Save(Now.AddMinutes(-10), [new(20, "kept", 300), new(22, "added", 70)]);

        // Act
        var forward = fixture.Archive.ReadFolderChanges("volume:A", older.Id, newer.Id, Token);
        var backward = fixture.Archive.ReadFolderChanges("volume:A", newer.Id, older.Id, Token);

        // Assert
        Assert.Equal(
            new BigInteger(200),
            Assert.Single(forward.Folders, row => row.Reference.RecordIndex == 20).AllocatedDelta
        );
        Assert.Equal(-forward.Root!.AllocatedDelta, backward.Root!.AllocatedDelta);
        Assert.Equal(newer.Id, backward.Baseline!.Id);
        Assert.Equal(older.Id, backward.Latest!.Id);
        Assert.False(backward.IsPartial);
        var kept = Assert.Single(backward.Folders, row => row.Reference.RecordIndex == 20);
        Assert.Equal(new BigInteger(-200), kept.AllocatedDelta);
        Assert.Equal(
            FolderChangeStatus.NotObservedAfter,
            Assert.Single(backward.Folders, row => row.Reference.RecordIndex == 22).Status
        );
        Assert.Equal(
            FolderChangeStatus.NotObservedBefore,
            Assert.Single(backward.Folders, row => row.Reference.RecordIndex == 21).Status
        );
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } =
            Path.Combine(
                Path.GetTempPath(),
                "something-changes-test-" + Guid.NewGuid().ToString("N")
            );
        public ScanHistoryArchive Archive { get; }

        public Fixture() => Archive = new(Root, new Clock(), TimeZoneInfo.Utc);

        public ScanHistoryEntry Save(
            DateTimeOffset ended,
            Folder[] folders,
            string key = "volume:A"
        )
        {
            var buffer = new BufferedScan(
                new(key, Guid.NewGuid(), 1, new(0, 1000), ended.AddSeconds(-1))
            );
            var root = FileReference.Create(5, 1);
            Add(
                new(
                    root,
                    true,
                    CapacityDomain.Ordinary,
                    CapacityResolution.Resolved,
                    [new(root, "root", FileNameNamespace.Win32, 0)],
                    [],
                    true
                )
            );
            foreach (var folder in folders)
            {
                var reference = FileReference.Create(folder.Index, folder.Sequence);
                Add(
                    new(
                        reference,
                        true,
                        CapacityDomain.Ordinary,
                        CapacityResolution.Resolved,
                        [new(root, folder.Name, FileNameNamespace.Win32, 0)],
                        [],
                        true
                    )
                );
                Add(
                    new(
                        FileReference.Create(folder.Index + 100, folder.Sequence),
                        false,
                        CapacityDomain.Ordinary,
                        folder.Unresolved
                            ? CapacityResolution.Unresolved
                            : CapacityResolution.Resolved,
                        [new(reference, "file", FileNameNamespace.Win32, 0)],
                        folder.Unresolved
                            ? []
                            : [new("", checked((long)folder.Bytes), checked((long)folder.Bytes))],
                        true
                    )
                );
            }
            buffer.Complete(Token);
            return Archive.Publish(
                buffer.Complete(TestContext.Current.CancellationToken),
                ended,
                "Demo",
                TimeSpan.FromSeconds(1),
                64UL * 1024 * 1024,
                Token
            );

            void Add(FileCapacityObservation file) =>
                buffer.Add(
                    new StagedObservation(
                        file.Reference.RecordIndex,
                        file.Resolution == CapacityResolution.Resolved
                            ? MftScanEntryKind.ResolvedFile
                            : MftScanEntryKind.Unresolved,
                        file.Resolution == CapacityResolution.Resolved
                            ? LiveFileReadStatus.ObservedUnchanged
                            : LiveFileReadStatus.Unsupported,
                        file
                    ),
                    Token
                );
        }

        public void Dispose()
        {
            string owned = Path.GetFullPath(Root);
            string parent =
                Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (
                !owned.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(owned)
                    .StartsWith("something-changes-test-", StringComparison.Ordinal)
            )
                throw new InvalidOperationException("Invalid cleanup path.");
            if (Directory.Exists(owned))
                Directory.Delete(owned, recursive: true);
        }
    }
}
