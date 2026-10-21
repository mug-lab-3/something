using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Something.Application;
using Something.Application.History;
using Something.Application.Scanning;
using Something.Ntfs;
using Something.Statistics;
using Something.Storage;
using Something.Windows;

namespace Something.Ntfs.Tests;

public sealed partial class ScanHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ScanHistoryEntry Entry(string key, DateTimeOffset ended, DateOnly? day = null) =>
        new(
            Guid.NewGuid(),
            key,
            "Demo",
            ended,
            day ?? DateOnly.FromDateTime(ended.DateTime),
            0,
            TimeSpan.FromSeconds(1)
        );

    [Fact]
    public void WindowsVolumeGuidCanBeReadWithoutScanningOrOpeningVolumeData()
    {
        if (!OperatingSystem.IsWindows())
            return;
        string drive = Path.GetPathRoot(Environment.SystemDirectory)![..2];

        string key = WindowsVolumeIdentity.ReadKey(drive);

        Assert.StartsWith("volume:", key);
        Assert.True(Guid.TryParseExact(key[7..], "N", out _));
        Assert.Equal(key, WindowsVolumeIdentity.ReadKey(drive));
    }

    [Fact]
    public void Retention_KeepsRecentCompletionsAndOnlyDailyLatestPerVolume()
    {
        var old1 = Entry("A", Now.AddDays(-8).AddHours(-2));
        var old2 = Entry("A", Now.AddDays(-8));
        var other = Entry("B", old1.CompletedUtc);
        var recent1 = Entry("A", Now.AddHours(-3));
        var recent2 = Entry("A", Now.AddHours(-1));

        var keep = HistoryRetention.Keep([old1, old2, other, recent1, recent2], Now);

        Assert.DoesNotContain(old1.Id, keep);
        Assert.Equal(4, keep.Count);
        Assert.All(new[] { old2, other, recent1, recent2 }, row => Assert.Contains(row.Id, keep));
    }

    [Fact]
    public void Retention_ExactSevenDayBoundaryIsConsolidatedAndRecentDailyLatestReplacesOlderRow()
    {
        var older = Entry("A", Now.AddDays(-7).AddTicks(-1));
        var boundary = Entry("A", Now.AddDays(-7));
        var recent = Entry("A", Now.AddDays(-7).AddTicks(1));

        var keep = HistoryRetention.Keep([older, boundary, recent], Now);

        Assert.Equal(new[] { recent.Id }, keep.ToArray());
    }

    [Fact]
    public void Retention_DoesNotConsolidateMultipleScansBetweenOneAndSevenDaysOld()
    {
        var entries = new[]
        {
            Entry("A", Now.AddDays(-2).AddHours(-1)),
            Entry("A", Now.AddDays(-2)),
            Entry("A", Now.AddDays(-6).AddHours(-1)),
            Entry("A", Now.AddDays(-6)),
            Entry("A", Now.AddDays(-7).AddTicks(1)),
        };

        var keep = HistoryRetention.Keep(entries, Now);

        Assert.True(keep.SetEquals(entries.Select(row => row.Id)));
    }

    [Fact]
    public void Retention_UsesCapturedCalendarDayAndDeterministicTies()
    {
        var first = Entry("A", Now.AddDays(-10), new(2026, 10, 4));
        var second = Entry("A", first.CompletedUtc, first.LocalDay);
        var nextDay = Entry("A", first.CompletedUtc.AddMinutes(1), new(2026, 10, 5));

        var keep = HistoryRetention.Keep([first, second, nextDay], Now);

        Assert.Equal(2, keep.Count);
        Assert.Contains(new[] { first.Id, second.Id }.Max(), keep);
        Assert.Contains(nextDay.Id, keep);
        Assert.True(keep.SetEquals(HistoryRetention.Keep([nextDay, second, first], Now)));
    }

    [Fact]
    public async Task SavedSnapshotSurvivesReopenAndRetainsNamesStreamsAndUncertaintyReadOnly()
    {
        using var fixture = new HistoryFixture(Now);
        var saved = fixture.Save(Now.AddMinutes(-1), Token);
        var reopened = new ScanHistoryArchive(fixture.Root, fixture.Clock, TimeZoneInfo.Utc);
        Assert.Equal(saved, Assert.Single(reopened.List("volume:A", Token)));
        await using var session = await TestHistory
            .For(reopened)
            .OpenAsync(saved.Id, "Demo", Token);
        var root = FileReference.Create(5, 1);

        var rows = await session.ReadFolderAsync(root, 0, false, Token, includeTotalCount: true);
        using var snapshot = reopened.Open(saved.Id, Token);
        var aggregation = snapshot.Store.ReadAggregation(saved.Id, Token);

        Assert.Equal(saved.CompletedLocal, session.CompletedLocal);
        Assert.Equal(3L, rows.TotalCount);
        Assert.Equal(2, rows.Entries.Count(row => row.Reference.RecordIndex == 20));
        Assert.DoesNotContain(rows.Entries, row => row.Name == "SHORT~1");
        Assert.Null(rows.Entries.Single(row => row.Reference.RecordIndex == 21).Data);
        Assert.Equal(8192UL, aggregation.Folders[root].SubtreeData.AllocatedBytes);
        var file = snapshot
            .Store.ReadFiles(saved.Id, Token)
            .Single(row => row.Reference.RecordIndex == 20);
        Assert.Equal((ushort)2, file.Reference.SequenceNumber);
        Assert.Equal(3, file.Names.Length);
        Assert.Equal(new[] { "", "extra" }, file.Streams.Select(stream => stream.Name).ToArray());
        var error = Assert.Throws<DbUpdateException>(() =>
            snapshot.Store.BeginScan(new("volume:B", Guid.NewGuid(), 1, new(0, 100), Now), Token)
        );
        Assert.Contains("cannot modify", error.Message);
    }

    [Fact]
    public void CalendarDateAndOffsetAreCapturedAtSaveAndSurviveTimeZoneChange()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Test+09",
            TimeSpan.FromHours(9),
            "Test+09",
            "Test+09"
        );
        using var fixture = new HistoryFixture(Now, zone);
        var first = fixture.Save(new(2026, 10, 13, 16, 0, 0, TimeSpan.Zero), Token);
        var second = fixture.Save(new(2026, 10, 14, 1, 0, 0, TimeSpan.Zero), Token);
        Assert.Equal(new DateOnly(2026, 10, 14), first.LocalDay);
        Assert.Equal(first.LocalDay, second.LocalDay);
        Assert.Equal(540, first.UtcOffsetMinutes);
        fixture.Clock.Now = Now.AddDays(8);
        var reopened = new ScanHistoryArchive(fixture.Root, fixture.Clock, TimeZoneInfo.Utc);

        reopened.Maintain(Token);

        Assert.Equal(second, Assert.Single(reopened.List("volume:A", Token)));
    }

    [Fact]
    public void DuplicateGenerationCannotOverwritePreviousHistory()
    {
        using var fixture = new HistoryFixture(Now);
        var previous = fixture.Save(Now.AddMinutes(-2), Token);
        using var reader = fixture.Archive.Open(previous.Id, Token);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Archive.Save(reader.Store, previous.Id, "Demo", TimeSpan.Zero, Token)
        );
        Assert.Equal(previous.Id, Assert.Single(fixture.Archive.List("volume:A", Token)).Id);
    }

    [Fact]
    public async Task ConcurrentSavesPublishIndependentCompletedSnapshots()
    {
        using var fixture = new HistoryFixture(Now);

        var saved = await Task.WhenAll(
            Task.Run(() => fixture.Save(Now.AddMinutes(-1), Token), Token),
            Task.Run(() => fixture.Save(Now, Token), Token)
        );

        Assert.Equal(2, fixture.Archive.List("volume:A", Token).Length);
        Assert.NotEqual(saved[0].Id, saved[1].Id);
        foreach (var row in saved)
        {
            using var snapshot = fixture.Archive.Open(row.Id, Token);
            Assert.Equal(row.Id, snapshot.Store.GetScan(row.Id).Id);
        }
    }

    [Fact]
    public void Maintenance_HidesExpiredHistoryAndDefersDeletionWhileReaderOwnsSnapshot()
    {
        using var fixture = new HistoryFixture(Now.AddDays(-8));
        var first = fixture.Save(Now.AddDays(-8).AddHours(-1), Token);
        var second = fixture.Save(Now.AddDays(-8), Token);
        using var reader = fixture.Archive.Open(first.Id, Token);
        fixture.Clock.Now = Now;

        fixture.Archive.Maintain(Token);

        Assert.Equal(second.Id, Assert.Single(fixture.Archive.List("volume:A", Token)).Id);
        Assert.Throws<InvalidOperationException>(() => fixture.Archive.Open(first.Id, Token));
        Assert.Equal(first.Id, reader.Store.GetScan(first.Id).Id);
        reader.Dispose();
        fixture.Archive.Maintain(Token);
        using var stored = ScanStore.OpenSharedHistoryReader(
            Path.Combine(fixture.Root, ScanHistoryArchive.DatabaseFileName),
            Token
        );
        Assert.Throws<KeyNotFoundException>(() => stored.GetScan(first.Id));
        Assert.Equal(second.Id, stored.GetScan(second.Id).Id);
    }

    [Fact]
    public void CancelledSaveAndIncompleteScanDoNotRemovePreviousHistory()
    {
        using var fixture = new HistoryFixture(Now);
        var previous = fixture.Save(Now.AddMinutes(-2), Token);
        using var source = ScanStore.OpenInMemory(Token);
        var pending = source.BeginScan(new("volume:A", Guid.NewGuid(), 1, new(0, 100), Now), Token);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Archive.Save(source, pending, "Demo", TimeSpan.Zero, Token)
        );
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => fixture.Save(Now, cancelled.Token));
        Assert.Equal(previous.Id, Assert.Single(fixture.Archive.List("volume:A", Token)).Id);
        Assert.Single(Directory.GetFiles(fixture.Root, "*.duckdb"));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "snapshots")));
    }

    [Fact]
    public void OldSqliteCatalogIsPreservedWithoutImportingOrDeletingSnapshotFiles()
    {
        using var fixture = new HistoryFixture(Now);
        Guid id = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "snapshots"));
        byte[] original = [1, 2, 3, 4];
        File.WriteAllBytes(fixture.PathFor(id), original);
        using (
            var catalog = SqliteDatabaseBootstrap.Open(
                Path.Combine(fixture.Root, "catalog.sqlite3"),
                Token
            )
        )
        {
            catalog.HistorySnapshots.Add(
                new()
                {
                    Id = id,
                    VolumeKey = "volume:A",
                    Drive = "Demo",
                    CompletedUtcTicks = Now.UtcTicks,
                    LocalDay = DateOnly.FromDateTime(Now.UtcDateTime).DayNumber,
                }
            );
            catalog.SaveChanges();
        }

        byte[] catalogBefore = File.ReadAllBytes(Path.Combine(fixture.Root, "catalog.sqlite3"));
        Assert.Empty(fixture.Archive.List("volume:A", Token));
        Assert.Equal(
            catalogBefore,
            File.ReadAllBytes(Path.Combine(fixture.Root, "catalog.sqlite3"))
        );
        Assert.Equal(original, File.ReadAllBytes(fixture.PathFor(id)));
        using var preserved = SqliteDatabaseBootstrap.Open(
            Path.Combine(fixture.Root, "catalog.sqlite3"),
            Token
        );
        Assert.Equal(id, Assert.Single(preserved.HistorySnapshots).Id);
        Assert.Empty(preserved.Scans);
    }

    [Fact]
    public void IndependentArchiveReaderDefersGenerationDeletion()
    {
        using var fixture = new HistoryFixture(Now.AddDays(-8));
        var first = fixture.Save(Now.AddDays(-8).AddHours(-1), Token);
        fixture.Save(Now.AddDays(-8), Token);
        using var reader = fixture.Archive.Open(first.Id, Token);
        fixture.Clock.Now = Now;
        var other = new ScanHistoryArchive(fixture.Root, fixture.Clock, TimeZoneInfo.Utc);

        other.Maintain(Token);
        Assert.Equal(first.Id, reader.Store.GetScan(first.Id).Id);
        reader.Dispose();
        other.Maintain(Token);

        using var remaining = ScanStore.OpenSharedHistoryReader(
            Path.Combine(fixture.Root, ScanHistoryArchive.DatabaseFileName),
            Token
        );
        Assert.Throws<KeyNotFoundException>(() => remaining.GetScan(first.Id));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class HistoryFixture : IDisposable
    {
        public string Root { get; } =
            Path.Combine(
                Path.GetTempPath(),
                "something-history-test-" + Guid.NewGuid().ToString("N")
            );
        public FixedClock Clock { get; }
        public ScanHistoryArchive Archive { get; }

        public HistoryFixture(DateTimeOffset now, TimeZoneInfo? timeZone = null)
        {
            Clock = new(now);
            Archive = new(Root, Clock, timeZone ?? TimeZoneInfo.Utc);
        }

        public string PathFor(Guid id) => Path.Combine(Root, "snapshots", $"{id:N}.sqlite3");

        public ScanHistoryEntry Save(
            DateTimeOffset ended,
            CancellationToken token,
            Action<Guid>? beforeSave = null
        )
        {
            using var source = ScanStore.OpenInMemory(token);
            var root = FileReference.Create(5, 1);
            var buffer = new BufferedScan(
                new("volume:A", Guid.NewGuid(), 99, new(0, 100), ended.AddMinutes(-1))
            );
            var directory = new FileCapacityObservation(
                root,
                true,
                CapacityDomain.Ordinary,
                CapacityResolution.Resolved,
                [new(root, "root", FileNameNamespace.Win32, 0)],
                [],
                true
            );
            var file = new FileCapacityObservation(
                FileReference.Create(20, 2),
                false,
                CapacityDomain.Ordinary,
                CapacityResolution.Resolved,
                [
                    new(root, "file", FileNameNamespace.Win32, 0),
                    new(root, "alias", FileNameNamespace.Win32, 0),
                    new(root, "SHORT~1", FileNameNamespace.Dos, 0),
                ],
                [new("", 7, 4096), new("extra", 99, 4096)],
                true
            );
            var unresolved = new FileCapacityObservation(
                FileReference.Create(21, 3),
                false,
                CapacityDomain.Ordinary,
                CapacityResolution.Unresolved,
                [new(root, "unknown", FileNameNamespace.Win32, 0)],
                [],
                true
            );
            foreach (var fact in new[] { directory, file, unresolved })
                buffer.Add(
                    new StagedObservation(
                        fact.Reference.RecordIndex,
                        fact.Resolution == CapacityResolution.Resolved
                            ? MftScanEntryKind.ResolvedFile
                            : MftScanEntryKind.Unresolved,
                        fact.Resolution == CapacityResolution.Resolved
                            ? LiveFileReadStatus.ObservedUnchanged
                            : LiveFileReadStatus.Unsupported,
                        fact
                    ),
                    token
                );
            buffer.Complete(token);
            var id = source.PublishBufferedScan(buffer, ended, token);
            beforeSave?.Invoke(id);
            return Archive.Save(source, id, "Demo", TimeSpan.FromSeconds(1), token);
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
                    .StartsWith("something-history-test-", StringComparison.Ordinal)
            )
                throw new InvalidOperationException("Invalid test cleanup path.");
            if (Directory.Exists(owned))
                Directory.Delete(owned, recursive: true);
        }
    }
}
