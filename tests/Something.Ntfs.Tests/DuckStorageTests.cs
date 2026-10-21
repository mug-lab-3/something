using System.Numerics;
using DuckDB.NET.Data;
using Microsoft.EntityFrameworkCore;
using Something.Application.Browsing;
using Something.Application.Scanning;
using Something.Statistics;
using Something.Storage;

namespace Something.Ntfs.Tests;

public sealed class DuckStorageTests
{
    internal static DuckDBException NativeAppenderMessage(string message) =>
        (DuckDBException)
            typeof(DuckDBException)
                .GetConstructor(
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic,
                    null,
                    [typeof(string)],
                    null
                )!
                .Invoke([message]);

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly FileReference Root = FileReference.Create(5, 1);

    [Fact]
    public void BrowserSizeSortKeepsDirectionAcrossAllPagesAndUnknownAtTheEnd()
    {
        using var database = TemporaryScanDatabase.Create(Token);
        var buffer = new BufferedScan(
            new("synthetic:sort", Guid.NewGuid(), 1, new(0, 1000), Start)
        );
        Add(
            new(
                Root,
                true,
                CapacityDomain.Ordinary,
                CapacityResolution.Resolved,
                [new(Root, "root", FileNameNamespace.Win32, 0)],
                [],
                true
            )
        );
        var directory = FileReference.Create(700, 1);
        Add(
            new(
                directory,
                true,
                CapacityDomain.Ordinary,
                CapacityResolution.Resolved,
                [new(Root, "directory", FileNameNamespace.Win32, 0)],
                [],
                true
            )
        );
        for (ulong index = 20; index < 620; index++)
            Add(
                new(
                    FileReference.Create(index, 1),
                    false,
                    CapacityDomain.Ordinary,
                    CapacityResolution.Resolved,
                    [new(Root, $"file-{index}", FileNameNamespace.Win32, 0)],
                    [new("", (long)index, (long)index)],
                    true
                )
            );
        Add(
            new(
                FileReference.Create(650, 1),
                false,
                CapacityDomain.Ordinary,
                CapacityResolution.Unresolved,
                [new(Root, "unknown", FileNameNamespace.Win32, 0)],
                [],
                true
            )
        );
        buffer.Complete(Token);
        var id = database.Store.PublishBufferedScan(buffer, Start.AddSeconds(1), Token);

        foreach (bool descending in new[] { true, false })
        {
            var rows = new List<FolderEntry>();
            for (int page = 0; ; page++)
            {
                var batch = database.Store.ReadFolderEntries(
                    id,
                    Root,
                    page,
                    cancellationToken: Token,
                    descending: descending
                );
                rows.AddRange(batch.Entries);
                if (!batch.HasMore)
                    break;
            }
            Assert.Equal(602, rows.Count);
            Assert.Equal(602, rows.Select(row => row.Reference).Distinct().Count());
            Assert.Equal(directory, rows[0].Reference);
            Assert.Null(rows[^1].Data);
            var expected = Enumerable.Range(20, 600).Select(index => (UInt128)index);
            Assert.Equal(
                descending ? expected.Reverse() : expected,
                rows.Skip(1).Take(600).Select(row => row.Data!.Value.AllocatedBytes)
            );
        }

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

    [Fact]
    public void RecoveryDoesNotRetryWhenCommitHasStarted()
    {
        using var database = TemporaryScanDatabase.Create(Token);
        int growthRequests = 0;

        Assert.Throws<DuckDBException>(() =>
            DuckMemoryRecovery.Run<int>(
                (DuckScanDbContext)database.Store.HistoryContext,
                report =>
                {
                    report?.Invoke(new(ScanWorkStage.Committing, 0, null));
                    throw CreateMemoryError();
                },
                limit =>
                {
                    growthRequests++;
                    return limit * 2;
                },
                null,
                Token
            )
        );

        Assert.Equal(0, growthRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommitFailureRetriesOnlyWhenTheDatabaseShowsNoPublication(bool committed)
    {
        using var database = TemporaryScanDatabase.Create(Token);
        int attempts = 0,
            checks = 0;

        int result = DuckMemoryRecovery.Run(
            (DuckScanDbContext)database.Store.HistoryContext,
            report =>
            {
                report?.Invoke(new(ScanWorkStage.Committing, 0, null));
                if (++attempts == 1)
                    throw CreateMemoryError();
                return 2;
            },
            limit => null,
            null,
            Token,
            readCommitted: () =>
            {
                checks++;
                return (committed, 1);
            }
        );

        Assert.Equal(1, checks);
        Assert.Equal(committed ? 1 : 2, attempts);
        Assert.Equal(committed ? 1 : 2, result);
    }

    [Fact]
    public void PublicationThatCommittedBeforeTheErrorIsReturnedWithoutRepublishing()
    {
        using var database = TemporaryScanDatabase.Create(Token);
        var store = database.Store;
        var buffer = Buffer();
        int attempts = 0;

        var id = store.PublishHistoryBuffer(
            buffer,
            Start.AddSeconds(1),
            new()
            {
                VolumeKey = "synthetic:A",
                Drive = "Synthetic",
                CompletedUtcTicks = Start.AddSeconds(1).UtcTicks,
                LocalDay = 739902,
            },
            Token,
            progress: p =>
            {
                // Simulates an error reported after the native commit already succeeded.
                if (p.Stage == ScanWorkStage.Committing && ++attempts == 1)
                {
                    var context = store.HistoryContext;
                    context.Database.CommitTransaction();
                    context.Database.BeginTransaction();
                    throw CreateMemoryError();
                }
            }
        );

        Assert.Equal(1, attempts);
        Assert.Single(store.HistoryContext.Scans);
        Assert.Equal(
            id,
            store.GetPublishedScan(buffer.Descriptor.VolumeKey, buffer.Descriptor.Range)
        );
    }

    private static DuckDBException CreateMemoryError() =>
        CreateQueryError("SELECT i, count(*) FROM range(100000) t(i) GROUP BY i", lowMemory: true);

    private static DuckDBException CreateQueryError(string sql, bool lowMemory = false)
    {
        // Get a real native error without allocating a large fixture or touching a live drive.
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        if (lowMemory)
        {
            using var settings = connection.CreateCommand();
            settings.CommandText = "SET memory_limit='1KiB'; SET temp_directory='';";
            settings.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Assert.Throws<DuckDBException>(() => command.ExecuteScalar());
    }

    [Fact]
    public void MemoryFailureRollsBackBeforeBudgetGrowthAndRetriesCompletedFactsOnce()
    {
        using var database = TemporaryScanDatabase.Create(Token);
        var store = database.Store;
        var buffer = Buffer();
        var previous = store.PublishBufferedScan(buffer, Start.AddSeconds(1), Token);
        var updates = new List<ScanWorkProgress>();
        int attempts = 0,
            increases = 0;

        var id = store.PublishHistoryBuffer(
            buffer,
            Start.AddSeconds(2),
            new()
            {
                VolumeKey = "synthetic:A",
                Drive = "Synthetic",
                CompletedUtcTicks = Start.AddSeconds(2).UtcTicks,
                LocalDay = 739902,
            },
            Token,
            (context, _) =>
            {
                if (++attempts == 1)
                    throw CreateMemoryError();
            },
            updates.Add,
            limit =>
            {
                increases++;
                Assert.Equal(1UL << 30, limit);
                Assert.Single(store.HistoryContext.Scans);
                Assert.Equal(
                    previous,
                    store.GetPublishedScan(buffer.Descriptor.VolumeKey, buffer.Descriptor.Range)
                );
                Assert.All(store.HistoryContext.Browser, row => Assert.Equal(previous, row.ScanId));
                return 2UL << 30;
            }
        );

        Assert.Equal(2, attempts);
        Assert.Equal(1, increases);
        Assert.Equal(2, store.HistoryContext.Scans.Count());
        Assert.Single(store.HistoryContext.HistorySnapshots);
        Assert.Equal(
            id,
            store.GetPublishedScan(buffer.Descriptor.VolumeKey, buffer.Descriptor.Range)
        );
        Assert.Contains(updates, p => p.RetryAttempt == 1 && p.MemoryLimitBytes == 2UL << 30);
        Assert.Equal(2, store.ReadFolderEntries(id, Root, cancellationToken: Token).Entries.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FailedRecoveryPreservesPreviousGenerationAndBoundsRetries(int failure)
    {
        using var database = TemporaryScanDatabase.Create(Token);
        var store = database.Store;
        var buffer = Buffer();
        var previous = store.PublishBufferedScan(buffer, Start.AddSeconds(1), Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        int attempts = 0;
        Action publish = () =>
            store.PublishHistoryBuffer(
                buffer,
                Start.AddSeconds(2),
                new()
                {
                    VolumeKey = "synthetic:A",
                    Drive = "Synthetic",
                    CompletedUtcTicks = Start.AddSeconds(2).UtcTicks,
                    LocalDay = 739902,
                },
                cancellation.Token,
                (_, _) =>
                {
                    attempts++;
                    if (failure == 2)
                        cancellation.Cancel();
                    throw CreateMemoryError();
                },
                reserveMemoryGrowth: limit => failure == 0 ? null : Math.Min(limit * 2, 8UL << 30)
            );

        if (failure == 2)
            Assert.Throws<OperationCanceledException>(publish);
        else
            Assert.Throws<DuckDBException>(publish);

        // Growth attempts first, then one disk-spill attempt; cancellation stops immediately.
        Assert.Equal(
            failure switch
            {
                0 => 2,
                1 => DuckMemoryRecovery.MaximumGrowthAttempts + 2,
                _ => 1,
            },
            attempts
        );
        Assert.Single(store.HistoryContext.Scans);
        Assert.Empty(store.HistoryContext.HistorySnapshots);
        Assert.Equal(
            previous,
            store.GetPublishedScan(buffer.Descriptor.VolumeKey, buffer.Descriptor.Range)
        );
    }

    [Fact]
    public void EngineBudgetIsReleasedWhenLastContextCloses()
    {
        using var database = TemporaryScanDatabase.Create(
            Token,
            boundedMemory: true,
            fileCacheBytes: 2UL << 30
        );
        database.Store.Dispose();
        using var fresh = ScanStore.OpenHistoryWriter(database.SnapshotPath, 1UL << 30, Token);
        Assert.Equal(
            1UL << 30,
            DuckDatabaseBootstrap.ReadMemoryLimit((DuckScanDbContext)fresh.HistoryContext)
        );
    }

    [Fact]
    public void MemoryRecoveryDoesNotTreatOtherErrorsAsRetryable()
    {
        Assert.False(DuckMemoryRecovery.IsOutOfMemory(new OutOfMemoryException()));
        Assert.False(
            DuckMemoryRecovery.IsOutOfMemory(CreateQueryError("SELECT nonexistent_column"))
        );
        Assert.True(
            DuckMemoryRecovery.IsOutOfMemory(new DbUpdateException("Wrapped", CreateMemoryError()))
        );
    }

    [Theory]
    [InlineData(
        "Failed to append: failed to allocate data of size 32.0 KiB (1.0 GiB/1.0 GiB used)",
        true
    )]
    [InlineData(
        "Failed to append: could not allocate block of size 256.0 KiB (1.0 GiB/1.0 GiB used)",
        true
    )]
    [InlineData(
        "TransactionContext Error: Failed to commit: failed to pin block of size 256.0 KiB (1023.8 MiB/1.0 GiB used)",
        true
    )]
    [InlineData("Failed to append: Current transaction is aborted (please ROLLBACK)", false)]
    [InlineData("Failed to append: CHECK constraint failed", false)]
    public void AppenderMemoryMessagesAreRecognizedWithoutTheQueryErrorPrefix(
        string message,
        bool expected
    )
    {
        var error = NativeAppenderMessage(message);
        Assert.Equal(expected, DuckMemoryRecovery.IsOutOfMemory(error));
        Assert.Equal(
            expected,
            DuckMemoryRecovery.IsOutOfMemory(new DbUpdateException("Wrapped", error))
        );
    }

    [Fact]
    public void LegacySmallCacheBudgetCannotConstrainSharedHistoryEngineTo64MiB()
    {
        using var database = TemporaryScanDatabase.Create(
            Token,
            boundedMemory: true,
            fileCacheBytes: 2UL << 30
        );
        var id = database.Store.PublishBufferedScan(Buffer(), Start.AddSeconds(1), Token);
        using var smallDrive = ScanStore.OpenHistoryWriter(
            database.SnapshotPath,
            64UL << 20,
            Token
        );
        using var settings = smallDrive.HistoryContext.Database.GetDbConnection().CreateCommand();
        settings.CommandText = "SELECT current_setting('memory_limit');";

        Assert.Equal("2.0 GiB", settings.ExecuteScalar());
        Assert.Equal(
            2,
            database.Store.ReadFolderEntries(id, Root, cancellationToken: Token).Entries.Length
        );
    }

    private static BufferedScan Buffer(
        string volume = "synthetic:A",
        Action<ScanWorkProgress>? progress = null
    )
    {
        var buffer = new BufferedScan(new(volume, Guid.NewGuid(), 1, new(0, 100), Start));
        foreach (
            var file in new FileCapacityObservation[]
            {
                new(
                    Root,
                    true,
                    CapacityDomain.Ordinary,
                    CapacityResolution.Resolved,
                    [new(Root, "root", FileNameNamespace.Win32, 0)],
                    [],
                    true
                ),
                new(
                    FileReference.Create(20, 7),
                    false,
                    CapacityDomain.Ordinary,
                    CapacityResolution.Resolved,
                    [
                        new(Root, "name\ud800\0", FileNameNamespace.Win32, 0),
                        new(Root, "alias", FileNameNamespace.Win32, 0),
                        new(Root, "SHORT~1", FileNameNamespace.Dos, 0),
                    ],
                    [new("", 5115, 8192), new("extra", 1, 4096)],
                    true
                ),
            }
        )
            buffer.Add(
                new StagedObservation(
                    file.Reference.RecordIndex,
                    MftScanEntryKind.ResolvedFile,
                    LiveFileReadStatus.ObservedUnchanged,
                    file
                ),
                Token
            );
        buffer.Complete(Token, progress);
        return buffer;
    }

    [Fact]
    public void CountedStagesReportRealTotalsAndNativeWorkRemainsIndeterminate()
    {
        var aggregation = new List<ScanWorkProgress>();
        var buffer = Buffer(progress: aggregation.Add);
        using var database = TemporaryScanDatabase.Create(Token);
        var publication = new List<ScanWorkProgress>();

        var id = database.Store.PublishBufferedScan(
            buffer,
            Start.AddSeconds(1),
            Token,
            publication.Add
        );

        AssertStage(aggregation, ScanWorkStage.CollectingFolders, 2);
        AssertStage(aggregation, ScanWorkStage.AggregatingFiles, 2);
        AssertStage(aggregation, ScanWorkStage.RollingUpFolders, 1);
        AssertStage(publication, ScanWorkStage.WritingFiles, 2);
        AssertStage(publication, ScanWorkStage.WritingFolders, 1);
        AssertStage(publication, ScanWorkStage.WritingFolderNames, 2);
        Assert.All(
            publication.Where(p =>
                p.Stage
                    is ScanWorkStage.FlushingRows
                        or ScanWorkStage.ValidatingRelationships
                        or ScanWorkStage.BuildingFolderList
                        or ScanWorkStage.Committing
            ),
            p => Assert.Null(p.Fraction)
        );
        Assert.Equal(ScanWorkStage.Committing, publication[^1].Stage);
        Assert.Equal(
            id,
            database.Store.GetPublishedScan(buffer.Descriptor.VolumeKey, buffer.Descriptor.Range)
        );

        static void AssertStage(List<ScanWorkProgress> updates, ScanWorkStage stage, long expected)
        {
            var rows = updates.Where(p => p.Stage == stage).ToArray();
            Assert.Equal(0, rows[0].Completed);
            Assert.Equal(expected, rows[^1].Completed);
            Assert.All(rows, p => Assert.Equal(expected, p.Total));
            Assert.Equal(1, rows[^1].Fraction);
            Assert.True(
                rows.Zip(rows.Skip(1)).All(pair => pair.First.Completed <= pair.Second.Completed)
            );
        }
    }

    [Fact]
    public void CancellationFromCountedProgressDoesNotReachCommitOrReplacePreviousGeneration()
    {
        using var database = TemporaryScanDatabase.Create(Token);
        var buffer = Buffer();
        var previous = database.Store.PublishBufferedScan(buffer, Start.AddSeconds(1), Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var updates = new List<ScanWorkProgress>();

        Assert.Throws<OperationCanceledException>(() =>
            database.Store.PublishBufferedScan(
                buffer,
                Start.AddSeconds(2),
                cancellation.Token,
                progress =>
                {
                    updates.Add(progress);
                    if (progress.Stage == ScanWorkStage.WritingFiles && progress.Completed > 0)
                        cancellation.Cancel();
                }
            )
        );

        Assert.DoesNotContain(updates, p => p.Stage == ScanWorkStage.Committing);
        Assert.Equal(
            previous,
            database.Store.GetPublishedScan(buffer.Descriptor.VolumeKey, buffer.Descriptor.Range)
        );
        Assert.Single(database.Store.HistoryContext.Scans);
    }

    [Fact]
    public void FileStoreUsesNativeColumnsAndBrowsableNamesWithoutDroppingStreams()
    {
        using var database = TemporaryScanDatabase.Create(Token);
        var id = database.Store.PublishBufferedScan(Buffer(), Start.AddSeconds(1), Token);

        var rows = database.Store.ReadFolderEntries(id, Root, cancellationToken: Token);
        var file = Assert.Single(
            database.Store.ReadFiles(id, Token),
            f => f.Reference.RecordIndex == 20
        );
        using var types = database.Store.HistoryContext.Database.GetDbConnection().CreateCommand();
        types.CommandText =
            "SELECT data_type FROM information_schema.columns WHERE table_name='folder_sample' AND column_name='SubtreeAllocated';";

        Assert.EndsWith(".duckdb", database.SnapshotPath);
        Assert.Equal("UHUGEINT", types.ExecuteScalar());
        Assert.Equal(2, rows.Entries.Length);
        Assert.Contains(
            rows.Entries,
            e => e.Name == "name\ud800\0" && e.Data!.Value.AllocatedBytes == 8192
        );
        Assert.Equal(3, file.Names.Length);
        Assert.Equal(2, file.Streams.Length);
        Assert.Equal((ushort)7, file.Reference.SequenceNumber);
    }

    [Fact]
    public void AppenderAndLinqPreserveFullUInt128Range()
    {
        using var database = TemporaryScanDatabase.Create(Token);
        var context = database.Store.HistoryContext;
        using var transaction = context.Database.BeginTransaction();
        var scan = ScanStore.CreateScanEntity(Buffer().Descriptor);
        context.Scans.Add(scan);
        context.SaveChanges();
        using (var insert = new ModelInsert<FolderSampleEntity>(context))
            insert.Add(
                new()
                {
                    ScanId = scan.Id,
                    RecordIndex = 5,
                    Sequence = 1,
                    DirectLogical = UInt128.MaxValue,
                    DirectAllocated = UInt128.MaxValue,
                },
                Token
            );

        var found = context
            .Folders.AsNoTracking()
            .Single(f => f.ScanId == scan.Id && f.DirectAllocated > (UInt128)long.MaxValue);

        Assert.Equal(UInt128.MaxValue, found.DirectLogical);
        Assert.Equal(UInt128.MaxValue, found.DirectAllocated);
        Assert.Equal(
            BigInteger.CreateChecked(UInt128.MaxValue),
            BigInteger.CreateChecked(found.DirectAllocated)
        );
        transaction.Rollback();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FailedCancelledOrOrphanedPublicationPreservesPreviousGeneration(int failure)
    {
        using var database = TemporaryScanDatabase.Create(Token);
        var store = database.Store;
        var buffer = Buffer();
        var previous = store.PublishBufferedScan(buffer, Start.AddSeconds(1), Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        void Publish() =>
            store.PublishHistoryBuffer(
                buffer,
                Start.AddSeconds(2),
                new()
                {
                    VolumeKey = "synthetic:A",
                    Drive = "Synthetic",
                    CompletedUtcTicks = Start.AddSeconds(2).UtcTicks,
                    LocalDay = 739902,
                },
                cancellation.Token,
                (context, id) =>
                {
                    if (failure == 0)
                        throw new InvalidOperationException("Injected failure.");
                    if (failure == 1)
                        cancellation.Cancel();
                    if (failure == 2)
                        context.Database.ExecuteSqlInterpolated(
                            $"INSERT INTO stream (ScanId,RecordIndex,Ordinal,Name,Logical,Allocated) VALUES ({id},99,0,{Array.Empty<byte>()},1,1)"
                        );
                }
            );

        if (failure == 0)
            Assert.Throws<InvalidOperationException>(Publish);
        if (failure == 1)
            Assert.Throws<OperationCanceledException>(Publish);
        if (failure == 2)
            Assert.Throws<InvalidDataException>(Publish);

        Assert.Equal(
            previous,
            store.GetPublishedScan(buffer.Descriptor.VolumeKey, buffer.Descriptor.Range)
        );
        Assert.Single(store.HistoryContext.Scans);
        Assert.All(store.HistoryContext.Browser, b => Assert.Equal(previous, b.ScanId));
        Assert.Equal(
            2,
            store.ReadFolderEntries(previous, Root, cancellationToken: Token).Entries.Length
        );
    }

    [Fact]
    public void ExplicitMemoryDiagnosticExportsToNewFileAndDoesNotOverwriteIt()
    {
        using var database = TemporaryScanDatabase.Create(Token, inMemory: true);
        var id = database.Store.PublishBufferedScan(Buffer(), Start.AddSeconds(1), Token);

        Assert.True(database.ExportSnapshot(Token) > 0);
        using var exported = database.OpenSnapshot(Token);
        Assert.Equal(
            2,
            exported.ReadFolderEntries(id, Root, cancellationToken: Token).Entries.Length
        );
        Assert.Throws<IOException>(() => database.ExportSnapshot(Token));
    }
}
