using Microsoft.EntityFrameworkCore;
using Something.Ntfs;
using Something.Statistics;
using Something.Storage;

namespace Something.Ntfs.Tests;

public sealed class SharedHistoryTests
{
    [Fact]
    public void ManualDeletionPreservesOtherScansSharedFactsAndIndependentReaders()
    {
        using var fixture = new Fixture();
        var first = fixture.Save(Buffer(), Now.AddMinutes(-3));
        var second = fixture.Save(Buffer(), Now.AddMinutes(-2));
        var otherVolume = fixture.Save(Buffer(key: "volume:B"), Now.AddMinutes(-1));
        var independent = new ScanHistoryArchive(fixture.RootPath, fixture.Clock, TimeZoneInfo.Utc);
        using var reader = independent.Open(first.Id, Token);

        Assert.Equal(
            new[] { first.Id },
            fixture.Archive.Delete([first.Id, first.Id, Guid.NewGuid()], Token)
        );
        Assert.Equal(
            new[] { otherVolume.Id, second.Id },
            fixture.Archive.ListAll(Token).Select(row => row.Id)
        );
        Assert.Throws<InvalidOperationException>(() => fixture.Archive.Open(first.Id, Token));
        Assert.Equal(1, fixture.Archive.CollectRetired(Token));
        Assert.NotEmpty(reader.Store.ReadFiles(first.Id, Token));
        reader.Dispose();

        Assert.Equal(0, fixture.Archive.CollectRetired(Token));
        using (var remaining = fixture.Writer())
        {
            var db = Assert.IsType<SharedHistoryDbContext>(remaining.HistoryContext);
            Assert.False(db.Scans.Any(row => row.Id == first.Id));
            Assert.NotEmpty(remaining.ReadFiles(second.Id, Token));
            Assert.NotEmpty(remaining.ReadFiles(otherVolume.Id, Token));
            Assert.Equal(
                ListingIds(db, second.Id).Length,
                db.Listings.Count(row => row.ScanId == second.Id)
            );
        }
        Assert.Empty(fixture.Archive.Delete([first.Id], Token));
        fixture.Archive.Delete([second.Id, otherVolume.Id], Token);
        Assert.Equal(0, fixture.Archive.CollectRetired(Token));
        Assert.Empty(fixture.Archive.ListAll(Token));
        using var empty = fixture.Writer();
        var context = Assert.IsType<SharedHistoryDbContext>(empty.HistoryContext);
        Assert.Empty(context.HistorySnapshots);
        Assert.Empty(context.Scans);
        Assert.Empty(context.States);
        Assert.Empty(context.Samples);
        Assert.Empty(context.Entries);
        Assert.Empty(context.Listings);
    }

    [Fact]
    public void ManualDeletionHandlesEmptyUnknownBatchedAndCancelledRequests()
    {
        using var fixture = new Fixture();
        var first = fixture.Save(Buffer(), Now.AddMinutes(-2));
        var second = fixture.Save(Buffer("size"), Now.AddMinutes(-1));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            fixture.Archive.Delete([first.Id, second.Id], cancelled.Token)
        );
        Assert.Equal(2, fixture.Archive.ListAll(Token).Length);
        Assert.Empty(fixture.Archive.Delete([], Token));
        var batch = Enumerable.Range(0, 257).Select(_ => Guid.NewGuid()).Append(first.Id).ToArray();
        Assert.Equal(new[] { first.Id }, fixture.Archive.Delete(batch, Token));
        Assert.Equal(second.Id, Assert.Single(fixture.Archive.ListAll(Token)).Id);
        fixture.Archive.CollectRetired(Token);
        using var remaining = fixture.Archive.Open(second.Id, Token);
        Assert.Equal(
            8192,
            remaining
                .Store.ReadFiles(second.Id, Token)
                .Single(row => row.Reference.RecordIndex == 1000)
                .Streams[0]
                .AllocatedBytes
        );
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly FileReference Root = FileReference.Create(5, 1);
    private static readonly FileReference Folder = FileReference.Create(20, 1);

    [Fact]
    public void UnchangedScanReusesFactsListingsAndSamplesAfterClosingAllConnections()
    {
        using var fixture = new Fixture();
        var first = fixture.Save(Buffer(), Now.AddMinutes(-2));
        using (var writer = fixture.Writer())
        {
            var db = Assert.IsType<SharedHistoryDbContext>(writer.HistoryContext);
            Assert.Equal(6, db.States.Count());
        }
        var second = fixture.Save(Buffer(), Now.AddMinutes(-1));
        using var store = fixture.Writer();
        var context = Assert.IsType<SharedHistoryDbContext>(store.HistoryContext);
        Assert.Equal(6, context.States.Count());
        Assert.Equal(3, context.Samples.Count());
        Assert.Equal(4, context.Entries.Count());
        Assert.Equal(ListingIds(context, first.Id), ListingIds(context, second.Id));
        Assert.Equal(6, context.Observations.Count(row => row.ScanId == second.Id));
        var file = store.ReadFiles(second.Id, Token).Single(f => f.Reference.RecordIndex == 1000);
        Assert.Equal("file-0-\ud800\0", file.Names[0].Name);
        Assert.Equal(3, file.Names.Length);
        Assert.Equal(2, file.Streams.Length);
        Assert.Equal((ushort)3, file.ObservedHardLinkCount);
        Assert.Equal(
            1L,
            store
                .ReadFolderEntries(
                    second.Id,
                    Folder,
                    includeTotalCount: true,
                    cancellationToken: Token
                )
                .TotalCount
        );
    }

    [Theory]
    [InlineData("size")]
    [InlineData("rename")]
    [InlineData("move")]
    [InlineData("sequence")]
    [InlineData("ads")]
    [InlineData("attributes")]
    [InlineData("header")]
    public void EveryStoredMetadataChangeCreatesANewStateWithoutChangingOldHistory(string change)
    {
        using var fixture = new Fixture();
        var before = fixture.Save(Buffer(), Now.AddMinutes(-2));
        var after = fixture.Save(Buffer(change), Now.AddMinutes(-1));
        using var store = fixture.Writer();
        var db = Assert.IsType<SharedHistoryDbContext>(store.HistoryContext);
        Assert.Equal(7, db.States.Count());
        var old = store.ReadFiles(before.Id, Token).Single(f => f.Reference.RecordIndex == 1000);
        var current = store.ReadFiles(after.Id, Token).Single(f => f.Reference.RecordIndex == 1000);
        Assert.Equal(4096, old.Streams[0].AllocatedBytes);
        Assert.Equal("file-0-\ud800\0", old.Names[0].Name);
        Assert.Equal((ushort)1, old.Reference.SequenceNumber);
        Assert.True(
            old.Names.AsSpan()
                .SequenceEqual(
                    Buffer().Observations.Single(o => o.RecordIndex == 1000).File!.Names.AsSpan()
                )
        );
        Assert.False(Equal(old, current));
        Assert.Equal(6, db.Observations.Count(o => o.ScanId == before.Id));
        Assert.Equal(6, db.Observations.Count(o => o.ScanId == after.Id));
        if (change == "size")
        {
            Assert.Equal(
                8192UL,
                store
                    .ReadFolderEntries(after.Id, Root, cancellationToken: Token)
                    .Entries.Single(e => e.Reference == Folder)
                    .Data!.Value.AllocatedBytes
            );
            Assert.Equal(
                4096UL,
                store
                    .ReadFolderEntries(before.Id, Root, cancellationToken: Token)
                    .Entries.Single(e => e.Reference == Folder)
                    .Data!.Value.AllocatedBytes
            );
        }
    }

    [Fact]
    public void UnobservedAndUnresolvedFilesDoNotBorrowTheirPreviousSizes()
    {
        using var fixture = new Fixture();
        var first = fixture.Save(Buffer(), Now.AddMinutes(-3));
        var missing = fixture.Save(Buffer("omit"), Now.AddMinutes(-2));
        var uncertain = fixture.Save(Buffer("unresolved"), Now.AddMinutes(-1));
        using var store = fixture.Writer();
        Assert.DoesNotContain(
            store.ReadFiles(missing.Id, Token),
            f => f.Reference.RecordIndex == 1000
        );
        Assert.Empty(store.ReadFolderEntries(missing.Id, Folder, cancellationToken: Token).Entries);
        var row = Assert.Single(
            store.ReadFolderEntries(uncertain.Id, Folder, cancellationToken: Token).Entries
        );
        Assert.Null(row.Data);
        Assert.True(row.CapacityIncomplete);
        Assert.Equal(
            4096,
            store
                .ReadFiles(first.Id, Token)
                .Single(f => f.Reference.RecordIndex == 1000)
                .Streams[0]
                .AllocatedBytes
        );
        Assert.Contains(
            store.HistoryContext.Observations.Where(o => o.ScanId == uncertain.Id),
            o => o.RecordIndex == 500 && o.Kind == MftScanEntryKind.ExtensionRecord
        );
    }

    [Fact]
    public void RetentionCollectsOnlyUnreferencedStatesAndHonorsOpenHistoryLeases()
    {
        using var fixture = new Fixture();
        fixture.Clock.Now = Now.AddDays(-2);
        var first = fixture.Save(Buffer(), Now.AddDays(-2).AddHours(-1));
        using var lease = fixture.Archive.Open(first.Id, Token);
        var second = fixture.Save(Buffer(), Now.AddDays(-2));
        fixture.Clock.Now = Now.AddDays(6);
        fixture.Archive.Maintain(Token);
        Assert.Equal(
            4096,
            lease
                .Store.ReadFiles(first.Id, Token)
                .Single(f => f.Reference.RecordIndex == 1000)
                .Streams[0]
                .AllocatedBytes
        );
        lease.Dispose();
        fixture.Archive.Maintain(Token);
        using var current = fixture.Archive.Open(second.Id, Token);
        Assert.Equal(
            4096,
            current
                .Store.ReadFiles(second.Id, Token)
                .Single(f => f.Reference.RecordIndex == 1000)
                .Streams[0]
                .AllocatedBytes
        );
        var third = fixture.Save(Buffer("size"), Now.AddMinutes(-2));
        fixture.Save(Buffer("rename"), Now.AddMinutes(-1));
        using var store = fixture.Writer();
        var db = Assert.IsType<SharedHistoryDbContext>(store.HistoryContext);
        Assert.Equal(8, db.States.Count());
        Assert.Throws<KeyNotFoundException>(() => store.GetScan(first.Id));
        Assert.Equal(
            8192,
            store
                .ReadFiles(third.Id, Token)
                .Single(f => f.Reference.RecordIndex == 1000)
                .Streams[0]
                .AllocatedBytes
        );
    }

    [Fact]
    public void CancelledPublicationRollsBackSharedRowsAndCanBeRetried()
    {
        using var fixture = new Fixture();
        var previous = fixture.Save(Buffer(), Now.AddMinutes(-2));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Assert.Throws<OperationCanceledException>(() =>
            fixture.Archive.Publish(
                Buffer("size"),
                Now.AddMinutes(-1),
                "Demo",
                TimeSpan.FromSeconds(1),
                1UL << 30,
                cancellation.Token,
                progress: p =>
                {
                    if (p.Stage == ScanWorkStage.BuildingFolderList)
                        cancellation.Cancel();
                }
            )
        );
        Assert.Equal(previous.Id, Assert.Single(fixture.Archive.List("volume:A", Token)).Id);
        using (var store = fixture.Writer())
        {
            var db = Assert.IsType<SharedHistoryDbContext>(store.HistoryContext);
            Assert.Equal(6, db.States.Count());
            Assert.Single(db.Scans);
        }
        var saved = fixture.Save(Buffer("size"), Now.AddMinutes(-1));
        using var snapshot = fixture.Archive.Open(saved.Id, Token);
        Assert.Equal(
            8192,
            snapshot
                .Store.ReadFiles(saved.Id, Token)
                .Single(f => f.Reference.RecordIndex == 1000)
                .Streams[0]
                .AllocatedBytes
        );
    }

    [Fact]
    public void DifferentVolumesDoNotShareRecordIdentities()
    {
        using var fixture = new Fixture();
        fixture.Save(Buffer(), Now.AddMinutes(-2));
        fixture.Save(Buffer(key: "volume:B"), Now.AddMinutes(-1));
        using var store = fixture.Writer();
        var db = Assert.IsType<SharedHistoryDbContext>(store.HistoryContext);
        Assert.Equal(12, db.States.Count());
        Assert.Equal(2, db.Published.Count());
    }

    [Fact]
    public void AppenderCleanupPreservesOriginalConstraintFailureInsteadOfAbortedTransaction()
    {
        using var store = ScanStore.OpenDuckMemory(Token);
        var db = Assert.IsType<DuckScanDbContext>(store.HistoryContext);
        using var transaction = db.Database.BeginTransaction();
        var error = Assert.Throws<DuckDB.NET.Data.DuckDBException>(() =>
        {
            using var rows = new DuckModelInsert<ObservationEntity>(db);
            using var names = new DuckModelInsert<FileNameEntity>(db);
            var id = Guid.NewGuid();
            rows.Add(
                new()
                {
                    ScanId = id,
                    RecordIndex = 32,
                    Kind = (MftScanEntryKind)99,
                    Status = LiveFileReadStatus.Unsupported,
                },
                Token
            );
            names.Add(
                new()
                {
                    ScanId = id,
                    RecordIndex = 32,
                    Ordinal = 0,
                    ParentIndex = 5,
                    ParentSequence = 1,
                    Name = "name",
                    Namespace = FileNameNamespace.Win32,
                },
                Token
            );
            rows.Dispose();
        });
        Assert.Contains("CHECK constraint failed", error.Message);
        Assert.DoesNotContain("transaction is aborted", error.Message);
        Assert.Same(error, db.AppendFailure);
        transaction.Rollback();
    }

    [Fact]
    public void AppenderCleanupPreservesNativeMemoryFailureForBudgetRecovery()
    {
        using var store = ScanStore.OpenDuckMemory(Token);
        var db = Assert.IsType<DuckScanDbContext>(store.HistoryContext);
        using var transaction = db.Database.BeginTransaction();
        var error = Assert.Throws<DuckDB.NET.Data.DuckDBException>(() =>
        {
            using var names = new DuckModelInsert<FileNameEntity>(db);
            using var streams = new DuckModelInsert<StreamEntity>(db);
            using var settings = db.Database.GetDbConnection().CreateCommand();
            settings.CommandText = "SET memory_limit='1MiB'; SET temp_directory='';";
            settings.ExecuteNonQuery();
            var id = Guid.NewGuid();
            string name = new('x', 2048);
            for (int i = 0; i < 10000; i++)
                names.Add(
                    new()
                    {
                        ScanId = id,
                        RecordIndex = i + 32,
                        Ordinal = 0,
                        ParentIndex = 5,
                        ParentSequence = 1,
                        Name = name,
                        Namespace = FileNameNamespace.Win32,
                    },
                    Token
                );
            names.Dispose();
        });
        Assert.True(DuckMemoryRecovery.IsOutOfMemory(error));
        Assert.DoesNotContain("transaction is aborted", error.Message);
        transaction.Rollback();
    }

    [Fact]
    public void AppenderRejectsIncompatibleNativeColumnWidthsBeforeWriting()
    {
        using var fixture = new Fixture();
        fixture.Save(Buffer(), Now.AddMinutes(-1));
        using var store = fixture.Writer();
        var db = Assert.IsType<SharedHistoryDbContext>(store.HistoryContext);
        using var transaction = db.Database.BeginTransaction();
        SharedHistoryPublication.Execute(
            db,
            "CREATE TEMP TABLE wrong_width AS SELECT * EXCLUDE(Attributes),Attributes::UINTEGER Attributes FROM state_file_name WHERE FALSE;"
        );
        Assert.Throws<InvalidDataException>(() =>
            new DuckModelInsert<FileNameEntity>(db, "wrong_width")
        );
        transaction.Rollback();
        Assert.Equal(6, db.States.Count());
    }

    [Fact]
    public void GarbageCollectionRemovesOldChangedFactsAfterLeaseRelease()
    {
        using var fixture = new Fixture();
        fixture.Clock.Now = Now.AddDays(-2);
        var old = fixture.Save(Buffer(), Now.AddDays(-2).AddHours(-1));
        using var lease = fixture.Archive.Open(old.Id, Token);
        var current = fixture.Save(Buffer("size"), Now.AddDays(-2));
        fixture.Clock.Now = Now.AddDays(6);
        fixture.Archive.Maintain(Token);
        using (var store = fixture.Writer())
            Assert.Equal(
                7,
                Assert.IsType<SharedHistoryDbContext>(store.HistoryContext).States.Count()
            );
        lease.Dispose();
        fixture.Archive.Maintain(Token);
        using var remaining = fixture.Archive.Open(current.Id, Token);
        var db = Assert.IsType<SharedHistoryDbContext>(remaining.Store.HistoryContext);
        Assert.Equal(6, db.States.Count());
        Assert.Equal(
            8192,
            remaining
                .Store.ReadFiles(current.Id, Token)
                .Single(f => f.Reference.RecordIndex == 1000)
                .Streams[0]
                .AllocatedBytes
        );
        Assert.False(db.StateStreams.Any(s => s.RecordIndex == 1000 && s.Allocated == 4096));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedPublicationRaisesMemoryBudgetAfterRollbackBeforeRetry(bool appenderMessage)
    {
        using var native = new DuckDB.NET.Data.DuckDBConnection("Data Source=:memory:");
        native.Open();
        using var command = native.CreateCommand();
        command.CommandText = "SET memory_limit='1KiB'; SET temp_directory='';";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT i,count(*) FROM range(100000) t(i) GROUP BY i";
        var error = Assert.Throws<DuckDB.NET.Data.DuckDBException>(() => command.ExecuteScalar());
        if (appenderMessage)
            error = DuckStorageTests.NativeAppenderMessage(
                "Failed to append: failed to allocate data of size 32.0 KiB (1.0 GiB/1.0 GiB used)"
            );
        using var fixture = new Fixture();
        var previous = fixture.Save(Buffer(), Now.AddMinutes(-2));
        int retries = 0;
        var saved = fixture.Archive.Publish(
            Buffer("size"),
            Now.AddMinutes(-1),
            "Demo",
            TimeSpan.FromSeconds(1),
            1UL << 30,
            Token,
            progress: p =>
            {
                if (p.Stage == ScanWorkStage.BuildingFolderList && p.RetryAttempt == 0)
                    throw error;
            },
            reserveMemoryGrowth: limit =>
            {
                using var inspector = fixture.Writer();
                var db = Assert.IsType<SharedHistoryDbContext>(inspector.HistoryContext);
                Assert.Single(db.Scans);
                Assert.Equal(6, db.States.Count());
                Assert.Equal(previous.Id, Assert.Single(db.Published).ScanId);
                retries++;
                return limit * 2;
            }
        );
        Assert.Equal(1, retries);
        using var result = fixture.Archive.Open(saved.Id, Token);
        Assert.Equal(
            7,
            Assert.IsType<SharedHistoryDbContext>(result.Store.HistoryContext).States.Count()
        );
        Assert.Equal(
            8192,
            result
                .Store.ReadFiles(saved.Id, Token)
                .Single(f => f.Reference.RecordIndex == 1000)
                .Streams[0]
                .AllocatedBytes
        );
        using var settings = result.Store.HistoryContext.Database.GetDbConnection().CreateCommand();
        settings.CommandText = "SELECT current_setting('temp_directory')";
        Assert.Equal("", settings.ExecuteScalar());
    }

    [Fact]
    public void LargeFolderCopiesOnlyTheChangedBlockAndKeepsPagingAndSortingCorrect()
    {
        using var fixture = new Fixture();
        var before = fixture.Save(Buffer(count: 5000, aliases: false), Now.AddMinutes(-2));
        var after = fixture.Save(Buffer("size", count: 5000, aliases: false), Now.AddMinutes(-1));
        using var store = fixture.Writer();
        var db = Assert.IsType<SharedHistoryDbContext>(store.HistoryContext);
        var old = db
            .Listings.Where(r => r.ScanId == before.Id && r.ParentIndex == 20)
            .OrderBy(r => r.Bucket)
            .ToArray();
        var current = db
            .Listings.Where(r => r.ScanId == after.Id && r.ParentIndex == 20)
            .OrderBy(r => r.Bucket)
            .ToArray();
        Assert.True(old.Length > 1);
        Assert.All(old, r => Assert.True(r.Bucket > 0));
        Assert.Single(old.Zip(current), pair => pair.First.ListingId != pair.Second.ListingId);
        var page = store.ReadFolderEntries(
            after.Id,
            Folder,
            cancellationToken: Token,
            includeTotalCount: true
        );
        Assert.Equal(5000L, page.TotalCount);
        Assert.True(page.HasMore);
        Assert.Equal(250, page.Entries.Length);
        Assert.Equal(1000UL, page.Entries[0].Reference.RecordIndex);
        Assert.Equal(8192UL, page.Entries[0].Data!.Value.AllocatedBytes);
        Assert.Equal(
            1000UL,
            store
                .ReadFolderEntries(before.Id, Folder, cancellationToken: Token)
                .Entries[0]
                .Reference.RecordIndex
        );
        Assert.Equal(
            4096UL,
            store
                .ReadFolderEntries(before.Id, Folder, cancellationToken: Token)
                .Entries[0]
                .Data!.Value.AllocatedBytes
        );
    }

    private static bool Equal(FileCapacityObservation a, FileCapacityObservation b) =>
        a.Reference == b.Reference
        && a.ObservedHardLinkCount == b.ObservedHardLinkCount
        && a.Names.AsSpan().SequenceEqual(b.Names.AsSpan())
        && a.Streams.AsSpan().SequenceEqual(b.Streams.AsSpan());

    private static Guid[] ListingIds(SharedHistoryDbContext db, Guid scan) =>
        db
            .Listings.Where(r => r.ScanId == scan)
            .OrderBy(r => r.ParentIndex)
            .ThenBy(r => r.ParentSequence)
            .ThenBy(r => r.Bucket)
            .Select(r => r.ListingId)
            .ToArray();

    private static BufferedScan Buffer(
        string change = "",
        string key = "volume:A",
        int count = 1,
        bool aliases = true
    )
    {
        var buffer = new BufferedScan(
            new(key, Guid.NewGuid(), 1, new(0, (ulong)count + 2000), Now.AddDays(-3))
        );
        Add(
            new(
                Root,
                true,
                CapacityDomain.Ordinary,
                CapacityResolution.Resolved,
                [new(Root, "root", FileNameNamespace.Win32AndDos, 0)],
                [],
                true
            )
        );
        Add(
            new(
                Folder,
                true,
                CapacityDomain.Ordinary,
                CapacityResolution.Resolved,
                [new(Root, "folder", FileNameNamespace.Win32AndDos, 0)],
                [],
                true
            )
        );
        var other = FileReference.Create(30, 1);
        Add(
            new(
                other,
                true,
                CapacityDomain.Ordinary,
                CapacityResolution.Resolved,
                [new(Root, "other", FileNameNamespace.Win32AndDos, 0)],
                [],
                true
            )
        );
        for (int i = 0; i < count; i++)
        {
            bool changed = i == 0;
            if (changed && change == "omit")
                continue;
            string name = $"file-{i}-\ud800\0" + (changed && change == "rename" ? "-new" : "");
            var parent = changed && change == "move" ? other : Folder;
            bool unresolved = changed && change == "unresolved";
            var names = new List<NtfsFileName>
            {
                new(
                    parent,
                    name,
                    FileNameNamespace.Win32,
                    changed && change == "attributes" ? 4U : 0U
                ),
            };
            if (aliases && !unresolved)
            {
                names.Add(new(parent, "SHORT~1", FileNameNamespace.Dos, 0));
                names.Add(new(other, "alias", FileNameNamespace.Win32AndDos, 0));
            }
            long size = changed && change == "size" ? 8192 : 4096;
            Add(
                new(
                    FileReference.Create(
                        (ulong)i + 1000,
                        changed && change == "sequence" ? (ushort)2 : (ushort)1
                    ),
                    false,
                    CapacityDomain.Ordinary,
                    unresolved ? CapacityResolution.Unresolved : CapacityResolution.Resolved,
                    [.. names],
                    unresolved
                        ? []
                        :
                        [
                            new("", size - 1, size),
                            new("ads", changed && change == "ads" ? 64 : 32, 0),
                        ],
                    true
                )
                {
                    ObservedHardLinkCount =
                        changed && change == "header" ? (ushort)9 : (ushort)names.Count,
                }
            );
        }
        buffer.Add(
            new StagedObservation(
                8,
                MftScanEntryKind.Unresolved,
                LiveFileReadStatus.Unsupported,
                null
            ),
            Token
        );
        buffer.Add(
            new StagedObservation(
                500,
                MftScanEntryKind.ExtensionRecord,
                LiveFileReadStatus.ObservedUnchanged,
                null
            ),
            Token
        );
        buffer.Complete(Token);
        return buffer;

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

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = SharedHistoryTests.Now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        internal string RootPath { get; } =
            Path.Combine(
                Path.GetTempPath(),
                "something-shared-test-" + Guid.NewGuid().ToString("N")
            );
        internal Clock Clock { get; } = new();
        internal ScanHistoryArchive Archive { get; }

        internal Fixture() => Archive = new(RootPath, Clock, TimeZoneInfo.Utc);

        internal ScanStore Writer() =>
            ScanStore.OpenSharedHistoryWriter(
                Path.Combine(RootPath, ScanHistoryArchive.DatabaseFileName),
                1UL << 30,
                Token
            );

        internal ScanHistoryEntry Save(BufferedScan buffer, DateTimeOffset ended) =>
            Archive.Publish(buffer, ended, "Demo", TimeSpan.FromSeconds(1), 1UL << 30, Token);

        public void Dispose()
        {
            string resolved = Path.GetFullPath(RootPath);
            string parent =
                Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (
                !resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved)
                    .StartsWith("something-shared-test-", StringComparison.Ordinal)
            )
                throw new InvalidOperationException("Invalid owned test cleanup path.");
            if (Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }
}
