using Microsoft.EntityFrameworkCore;
using Something.Application.History;
using Something.Application.Scanning;
using Something.Statistics;
using Something.Storage;

namespace Something.Ntfs.Tests;

public sealed class SharedHistoryTests
{
    [Fact]
    public void SequentialFactsPreserveAllNamesAndStreamsAcrossSharedGenerationsAndEarlyDisposal()
    {
        using var fixture = new Fixture();
        var before = fixture.Save(Buffer(count: 2505, aliases: true), Now.AddMinutes(-3));
        var after = fixture.Save(Buffer("size", count: 2505, aliases: true), Now.AddMinutes(-2));
        using var store = fixture.Writer();
        var baseline = store.ReadFiles(before.Id, Token).ToArray();
        var current = store.ReadFiles(after.Id, Token).ToArray();

        Assert.Equal(2508, baseline.Length);
        Assert.Equal(baseline.Select(f => f.Reference), current.Select(f => f.Reference));
        foreach (var file in current.Where(f => !f.IsDirectory))
        {
            Assert.Equal(3, file.Names.Length);
            Assert.Equal(2, file.Streams.Length);
            Assert.Contains(file.Names, n => n.Namespace == FileNameNamespace.Dos);
        }
        Assert.Equal(
            4096,
            baseline.Single(f => f.Reference.RecordIndex == 1000).Streams[0].AllocatedBytes
        );
        Assert.Equal(
            8192,
            current.Single(f => f.Reference.RecordIndex == 1000).Streams[0].AllocatedBytes
        );
        Assert.Single(store.ReadFiles(after.Id, Token).Take(1));
        Assert.Equal(current.Length, store.ReadFiles(after.Id, Token).Count());
        using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token))
        using (var interrupted = store.ReadFiles(after.Id, cancellation.Token).GetEnumerator())
        {
            Assert.True(interrupted.MoveNext());
            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => interrupted.MoveNext());
        }
        Assert.Equal(current.Length, store.ReadFiles(after.Id, Token).Count());
        var all = store.ReadAllFolderEntries(after.Id, Folder, Token);
        Assert.Equal(2505, all.Length);
        Assert.Equal(2505, all.Select(e => (e.Reference, e.NameOrdinal)).Distinct().Count());
        Assert.Equal(8192UL, all[0].Data!.Value.AllocatedBytes);
        Assert.Equal(3, store.ReadVisibilityFolders(after.Id, Token).Count());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(4u)]
    [InlineData(6u)]
    [InlineData(uint.MaxValue)]
    public void StandardAttributesRoundTripAndInvalidateOnlyChangedStates(uint flags)
    {
        using var fixture = new Fixture();
        var before = fixture.Save(Buffer(), Now.AddMinutes(-3));
        var after = fixture.Save(Buffer("standard", standardAttributes: flags), Now.AddMinutes(-2));
        var same = fixture.Save(Buffer("standard", standardAttributes: flags), Now.AddMinutes(-1));
        using var store = fixture.Writer();
        var db = Assert.IsType<SharedHistoryDbContext>(store.HistoryContext);
        Assert.Equal(7, db.States.Count());
        var prior = store
            .ReadFiles(before.Id, Token)
            .Single(file => file.Reference.RecordIndex == 1000);
        Assert.Null(prior.StandardFileAttributes);
        Assert.Null(prior.IsHidden);
        var current = store
            .ReadFiles(after.Id, Token)
            .Single(file => file.Reference.RecordIndex == 1000);
        Assert.Equal(flags, current.StandardFileAttributes);
        Assert.Equal(0u, current.Names[0].FileAttributes);
        Assert.Equal((flags & 2) != 0, current.IsHidden);
        Assert.Equal((flags & 4) != 0, current.IsSystem);
        Assert.Null(
            Assert
                .Single(
                    store.ReadFolderEntries(before.Id, Folder, cancellationToken: Token).Entries
                )
                .StandardFileAttributes
        );
        foreach (var scan in new[] { after.Id, same.Id })
        {
            Assert.Equal(
                flags,
                Assert
                    .Single(store.ReadFolderEntries(scan, Folder, cancellationToken: Token).Entries)
                    .StandardFileAttributes
            );
            Assert.Equal(
                flags,
                Assert
                    .Single(
                        store
                            .ReadFolderEntries(
                                scan,
                                FileReference.Create(30, 1),
                                cancellationToken: Token
                            )
                            .Entries
                    )
                    .StandardFileAttributes
            );
        }
    }

    [Fact]
    public void InvalidCompactionBackupCannotOverwriteAnIntactDatabase()
    {
        using var fixture = new Fixture();
        var saved = fixture.Save(Buffer(), Now.AddMinutes(-1));
        string database = Path.Combine(fixture.RootPath, ScanHistoryArchive.DatabaseFileName);
        byte[] original = File.ReadAllBytes(database);
        File.WriteAllBytes(database + ".compaction-backup", "invalid backup"u8.ToArray());

        Assert.ThrowsAny<System.Data.Common.DbException>(() => fixture.Archive.ListAll(Token));

        Assert.Equal(original, File.ReadAllBytes(database));
        Assert.True(File.Exists(database + ".compaction-backup"));
        File.Delete(database + ".compaction-backup");
        Assert.Equal(saved.Id, Assert.Single(fixture.Archive.ListAll(Token)).Id);
    }

    [Fact]
    public void CompactionShrinksHolesAndPreservesSharedFactsNamesStreamsAndOtherVolumes()
    {
        using var fixture = new Fixture();
        var first = fixture.Save(Buffer(), Now.AddMinutes(-3));
        var second = fixture.Save(Buffer("size"), Now.AddMinutes(-2));
        var foreign = fixture.Save(Buffer(key: "volume:B"), Now.AddMinutes(-1));
        using (var writer = fixture.Writer())
        {
            writer.HistoryContext.Database.ExecuteSqlRaw(
                "CREATE TABLE unused_payload AS SELECT i, sha256(i::VARCHAR)||sha256((i+1)::VARCHAR)||sha256((i+2)::VARCHAR) v FROM range(30000) t(i); CHECKPOINT; CREATE TABLE kept_payload AS SELECT i,sha256(i::VARCHAR) v FROM range(12000) t(i); CHECKPOINT; DROP TABLE unused_payload;"
            );
        }
        fixture.Archive.Delete([first.Id], Token);

        var result = fixture.Archive.Compact(256UL << 20, Token);

        Assert.True(result.Replaced);
        Assert.True(result.ReclaimedBytes > 0);
        Assert.Equal(
            new[] { foreign.Id, second.Id },
            fixture.Archive.ListAll(Token).Select(row => row.Id)
        );
        using var remaining = fixture.Archive.Open(second.Id, Token);
        var file = remaining
            .Store.ReadFiles(second.Id, Token)
            .Single(row => row.Reference.RecordIndex == 1000);
        Assert.Equal("file-0-\ud800\0", file.Names[0].Name);
        Assert.Equal(3, file.Names.Length);
        Assert.Equal(8192, file.Streams[0].AllocatedBytes);
        Assert.Equal(2, file.Streams.Length);
        Assert.False(
            File.Exists(
                Path.Combine(fixture.RootPath, ScanHistoryArchive.DatabaseFileName)
                    + ".compaction-backup"
            )
        );
    }

    [Fact]
    public void CompactionRejectsIndependentReadersAndCancellationWithoutLosingHistory()
    {
        using var fixture = new Fixture();
        var saved = fixture.Save(Buffer(), Now.AddMinutes(-1));
        var other = new ScanHistoryArchive(fixture.RootPath);
        using (var reader = other.Open(saved.Id, Token))
            Assert.Equal(
                HistoryFailure.DatabaseInUse,
                HistoryFailures.Of(
                    Assert.Throws<InvalidOperationException>(() =>
                        fixture.Archive.Compact(256UL << 20, Token)
                    )
                )
            );
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Assert.Throws<OperationCanceledException>(() =>
            fixture.Archive.Compact(
                256UL << 20,
                cancelled.Token,
                stage =>
                {
                    if (stage == HistoryCompactionStage.ValidatingCopy)
                        cancelled.Cancel();
                }
            )
        );
        Assert.Equal(saved.Id, Assert.Single(fixture.Archive.ListAll(Token)).Id);
        Assert.Empty(Directory.EnumerateDirectories(fixture.RootPath, ".compaction-*"));
    }

    [Fact]
    public void InterruptedReplacementRestoresOriginalBackupBeforeOpeningHistory()
    {
        using var fixture = new Fixture();
        var saved = fixture.Save(Buffer(), Now.AddMinutes(-1));
        string database = Path.Combine(fixture.RootPath, ScanHistoryArchive.DatabaseFileName);
        File.Move(database, database + ".compaction-backup");
        File.WriteAllBytes(database, "incomplete replacement"u8.ToArray());

        Assert.Equal(saved.Id, Assert.Single(fixture.Archive.ListAll(Token)).Id);

        Assert.False(File.Exists(database + ".compaction-backup"));
        using var reopened = fixture.Archive.Open(saved.Id, Token);
        Assert.NotEmpty(reopened.Store.ReadFiles(saved.Id, Token));
    }

    [Fact]
    public void RequestedHistoryCountsExcludeMissingUnrequestedAndRetiredScans()
    {
        using var fixture = new Fixture();
        var first = fixture.Save(Buffer(), Now.AddMinutes(-3));
        var second = fixture.Save(Buffer(), Now.AddMinutes(-2));
        fixture.Save(Buffer(key: "volume:B"), Now.AddMinutes(-1));

        var counts = fixture.Archive.ReadRecordCounts([first.Id, first.Id, Guid.NewGuid()], Token);

        Assert.Equal(6L, Assert.Single(counts).Value);
        Assert.True(counts.ContainsKey(first.Id));
        Assert.Empty(fixture.Archive.ReadRecordCounts([], Token));
        fixture.Archive.Delete([first.Id], Token);
        counts = fixture.Archive.ReadRecordCounts([first.Id, second.Id], Token);
        Assert.Equal(second.Id, Assert.Single(counts).Key);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            fixture.Archive.ReadRecordCounts([], cancelled.Token)
        );
    }

    [Fact]
    public void HistorySummariesCountEachObservationOnceAndExcludeOtherVolumesAndRetiredScans()
    {
        using var fixture = new Fixture();
        var first = fixture.Save(Buffer(), Now.AddMinutes(-3));
        var second = fixture.Save(Buffer(), Now.AddMinutes(-2));
        fixture.Save(Buffer(key: "volume:B"), Now.AddMinutes(-1));

        var summaries = fixture.Archive.ListSummaries("volume:A", Token);

        Assert.Equal(new[] { second.Id, first.Id }, summaries.Select(row => row.Entry.Id));
        Assert.All(summaries, row => Assert.Equal(6L, row.RecordCount));
        using (var store = fixture.Writer())
            Assert.Equal(
                6L,
                store.HistoryContext.Observations.LongCount(row => row.ScanId == second.Id)
            );
        fixture.Archive.Delete([first.Id], Token);
        var remaining = Assert.Single(fixture.Archive.ListSummaries("volume:A", Token));
        Assert.Equal(second.Id, remaining.Entry.Id);
        Assert.Equal(6L, remaining.RecordCount);
        Assert.Empty(fixture.Archive.ListSummaries("missing", Token));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            fixture.Archive.ListSummaries("volume:A", cancelled.Token)
        );
    }

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
                db.Listings.Count(row =>
                    row.Generation == SharedHistoryPublication.GenerationOf(db, second.Id)
                )
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
    [InlineData("standard")]
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
                    Buffer()
                        .Complete(TestContext.Current.CancellationToken)
                        .Observations.Single(o => o.RecordIndex == 1000)
                        .File!.Names.AsSpan()
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
                Buffer("size").Complete(TestContext.Current.CancellationToken),
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
            new DuckModelInsert<FileNameEntity>(db, "wrong_width", ("Version", 1))
        );
        // A state table cannot be written without naming the integer version column.
        Assert.Throws<InvalidDataException>(() =>
            new DuckModelInsert<FileNameEntity>(db, "state_file_name")
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
            Buffer("size").Complete(TestContext.Current.CancellationToken),
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
    public void SharedPublicationSpillsToDiskOnlyAfterBudgetGrowthIsUnavailable()
    {
        using var fixture = new Fixture();
        var previous = fixture.Save(Buffer(), Now.AddMinutes(-2));
        string spill = DuckDatabaseBootstrap.SpillDirectory(fixture.Archive.DatabasePath);
        var error = DuckStorageTests.NativeAppenderMessage(
            "Failed to append: could not allocate block of size 256.0 KiB (1.0 GiB/1.0 GiB used)"
        );
        int growthRequests = 0;
        var observed = new List<(int Attempt, bool Spilling, string Setting)>();
        var saved = fixture.Archive.Publish(
            Buffer("size").Complete(TestContext.Current.CancellationToken),
            Now.AddMinutes(-1),
            "Demo",
            TimeSpan.FromSeconds(1),
            1UL << 30,
            Token,
            progress: p =>
            {
                if (p.Stage != ScanWorkStage.BuildingFolderList || p.Completed != 0)
                    return;
                using var inspector = fixture.Writer();
                using var command = inspector
                    .HistoryContext.Database.GetDbConnection()
                    .CreateCommand();
                command.CommandText = "SELECT current_setting('temp_directory')";
                observed.Add((p.RetryAttempt, p.SpillingToDisk, (string)command.ExecuteScalar()!));
                if (p.RetryAttempt == 0)
                    throw error;
            },
            reserveMemoryGrowth: _ =>
            {
                growthRequests++;
                return null;
            }
        );
        Assert.Equal(1, growthRequests);
        Assert.Equal(2, observed.Count);
        Assert.Equal((0, false, ""), observed[0]);
        Assert.Equal((1, true, spill), observed[1]);
        Assert.False(Directory.Exists(spill));
        using var result = fixture.Archive.Open(saved.Id, Token);
        Assert.Equal(
            8192,
            result
                .Store.ReadFiles(saved.Id, Token)
                .Single(f => f.Reference.RecordIndex == 1000)
                .Streams[0]
                .AllocatedBytes
        );
        Assert.NotEqual(previous.Id, saved.Id);
    }

    [Fact]
    public void SpillFallbackIsBoundedToOneDiskAttempt()
    {
        using var fixture = new Fixture();
        var previous = fixture.Save(Buffer(), Now.AddMinutes(-2));
        var error = DuckStorageTests.NativeAppenderMessage(
            "Failed to append: could not allocate block of size 256.0 KiB (1.0 GiB/1.0 GiB used)"
        );
        int attempts = 0;
        Assert.Throws<DuckDB.NET.Data.DuckDBException>(() =>
            fixture.Archive.Publish(
                Buffer("size").Complete(TestContext.Current.CancellationToken),
                Now.AddMinutes(-1),
                "Demo",
                TimeSpan.FromSeconds(1),
                1UL << 30,
                Token,
                progress: p =>
                {
                    if (p.Stage == ScanWorkStage.BuildingFolderList)
                    {
                        attempts++;
                        throw error;
                    }
                }
            )
        );
        Assert.Equal(2, attempts);
        Assert.False(
            Directory.Exists(DuckDatabaseBootstrap.SpillDirectory(fixture.Archive.DatabasePath))
        );
        Assert.Equal(
            previous.Id,
            Assert.Single(fixture.Archive.List(previous.VolumeKey, Token)).Id
        );
    }

    [Fact]
    public void StaleSpillDirectoryIsRemovedWhenTheEngineOpens()
    {
        using var fixture = new Fixture();
        string spill = DuckDatabaseBootstrap.SpillDirectory(
            Path.GetFullPath(fixture.Archive.DatabasePath)
        );
        Directory.CreateDirectory(spill);
        File.WriteAllText(Path.Combine(spill, "duckdb_temp_storage-0.tmp"), "stale");
        fixture.Save(Buffer(), Now.AddMinutes(-1));
        Assert.False(Directory.Exists(spill));
    }

    [Theory]
    [InlineData(true, false, "extra-stream")]
    [InlineData(false, true, "")]
    [InlineData(true, true, "")]
    public void ChangedNameOrStreamCountsCreateNewStatesInEitherDirection(
        bool beforeAliases,
        bool afterAliases,
        string afterChange
    )
    {
        using var fixture = new Fixture();
        var before = fixture.Save(Buffer(aliases: beforeAliases), Now.AddMinutes(-2));
        var after = fixture.Save(Buffer(afterChange, aliases: afterAliases), Now.AddMinutes(-1));
        using var store = fixture.Writer();
        var db = Assert.IsType<SharedHistoryDbContext>(store.HistoryContext);
        var old = store.ReadFiles(before.Id, Token).Single(f => f.Reference.RecordIndex == 1000);
        var current = store.ReadFiles(after.Id, Token).Single(f => f.Reference.RecordIndex == 1000);
        Assert.Equal(beforeAliases ? 3 : 1, old.Names.Length);
        Assert.Equal(afterAliases ? 3 : 1, current.Names.Length);
        Assert.Equal(afterChange == "extra-stream" ? 3 : 2, current.Streams.Length);
        bool changed = beforeAliases != afterAliases || afterChange.Length != 0;
        Assert.Equal(changed ? 7 : 6, db.States.Count());
        Assert.Equal(changed, !Equal(old, current));
    }

    [Fact]
    public void DuplicateStreamNamesAreRejectedWithoutChangingHistory()
    {
        using var fixture = new Fixture();
        var previous = fixture.Save(Buffer(), Now.AddMinutes(-2));
        Assert.Throws<InvalidDataException>(() =>
            fixture.Save(Buffer("duplicate-stream"), Now.AddMinutes(-1))
        );
        using var store = fixture.Writer();
        var db = Assert.IsType<SharedHistoryDbContext>(store.HistoryContext);
        Assert.Equal(previous.Id, Assert.Single(db.Published).ScanId);
        Assert.Equal(6, db.States.Count());
        Assert.Equal(6, db.Members.Count());
    }

    [Fact]
    public void ValidationRejectsMembersWithoutExactlyOneState()
    {
        using var fixture = new Fixture();
        var saved = fixture.Save(Buffer(), Now.AddMinutes(-1));
        using var store = fixture.Writer();
        var db = Assert.IsType<SharedHistoryDbContext>(store.HistoryContext);
        using (var transaction = db.Database.BeginTransaction())
        {
            SharedHistoryPublication.Execute(
                db,
                "INSERT INTO state_observation SELECT * FROM state_observation WHERE RecordIndex=1000"
            );
            db.DirtyGenerations.Add(saved.Id);
            Assert.Throws<InvalidDataException>(() =>
                SharedHistoryPublication.Validate(db, Token, null)
            );
            transaction.Rollback();
        }
        db.DirtyGenerations.Clear();
        using (var transaction = db.Database.BeginTransaction())
        {
            SharedHistoryPublication.Execute(
                db,
                "DELETE FROM state_observation WHERE RecordIndex=1000"
            );
            db.DirtyGenerations.Add(saved.Id);
            Assert.Throws<InvalidDataException>(() =>
                SharedHistoryPublication.Validate(db, Token, null)
            );
            transaction.Rollback();
        }
    }

    [Fact]
    public void RepeatedPreviousMembershipIsRejectedInsteadOfGuessingAVersion()
    {
        using var fixture = new Fixture();
        var previous = fixture.Save(Buffer(), Now.AddMinutes(-2));
        using (var store = fixture.Writer())
        {
            var db = Assert.IsType<SharedHistoryDbContext>(store.HistoryContext);
            SharedHistoryPublication.Execute(
                db,
                "INSERT INTO generation_observation SELECT * FROM generation_observation WHERE RecordIndex=1000"
            );
        }
        Assert.Throws<InvalidDataException>(() => fixture.Save(Buffer("size"), Now.AddMinutes(-1)));
        using var check = fixture.Writer();
        Assert.Equal(
            previous.Id,
            Assert
                .Single(Assert.IsType<SharedHistoryDbContext>(check.HistoryContext).Published)
                .ScanId
        );
    }

    [Fact]
    public void LargeSharedTablesHaveNoResidentKeysOrIndexes()
    {
        using var fixture = new Fixture();
        fixture.Save(Buffer(), Now.AddMinutes(-1));
        using var store = fixture.Writer();
        using var command = store.HistoryContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM (
                SELECT table_name FROM duckdb_constraints() WHERE constraint_type IN ('PRIMARY KEY','UNIQUE','FOREIGN KEY')
                UNION ALL SELECT table_name FROM duckdb_indexes())
            WHERE table_name IN ('state_observation','state_file_name','state_stream','state_folder_sample',
                'generation_observation','scan_folder_ref','scan_listing_ref','shared_entry')
            """;
        Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()));
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
            .Listings.Where(r =>
                r.Generation == SharedHistoryPublication.GenerationOf(db, before.Id)
                && r.ParentIndex == 20
            )
            .OrderBy(r => r.Bucket)
            .ToArray();
        var current = db
            .Listings.Where(r =>
                r.Generation == SharedHistoryPublication.GenerationOf(db, after.Id)
                && r.ParentIndex == 20
            )
            .OrderBy(r => r.Bucket)
            .ToArray();
        Assert.True(old.Length > 1);
        Assert.All(old, r => Assert.True(r.Bucket > 0));
        Assert.Single(old.Zip(current), pair => pair.First.Listing != pair.Second.Listing);
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
        && a.StandardFileAttributes == b.StandardFileAttributes
        && a.ObservedHardLinkCount == b.ObservedHardLinkCount
        && a.Names.AsSpan().SequenceEqual(b.Names.AsSpan())
        && a.Streams.AsSpan().SequenceEqual(b.Streams.AsSpan());

    private static long[] ListingIds(SharedHistoryDbContext db, Guid scan) =>
        db
            .Listings.Where(r => r.Generation == SharedHistoryPublication.GenerationOf(db, scan))
            .OrderBy(r => r.ParentIndex)
            .ThenBy(r => r.ParentSequence)
            .ThenBy(r => r.Bucket)
            .Select(r => r.Listing)
            .ToArray();

    private static BufferedScan Buffer(
        string change = "",
        string key = "volume:A",
        int count = 1,
        bool aliases = true,
        uint? standardAttributes = null
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
                    unresolved ? []
                        : changed && change == "duplicate-stream"
                            ? [new("", size - 1, size), new("ads", 32, 0), new("ads", 32, 0)]
                        : changed && change == "extra-stream"
                            ? [new("", size - 1, size), new("ads", 32, 0), new("more", 1, 0)]
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
                    StandardFileAttributes =
                        changed && change == "standard" ? standardAttributes ?? 6u : null,
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
            Archive.Publish(
                buffer.Complete(TestContext.Current.CancellationToken),
                ended,
                "Demo",
                TimeSpan.FromSeconds(1),
                1UL << 30,
                Token
            );

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
