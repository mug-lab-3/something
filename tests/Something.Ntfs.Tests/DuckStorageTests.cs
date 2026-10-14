using System.Numerics;
using Microsoft.EntityFrameworkCore;
using Something.Ntfs;
using Something.Statistics;
using Something.Storage;

namespace Something.Ntfs.Tests;

public sealed class DuckStorageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly FileReference Root = FileReference.Create(5, 1);

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

        Assert.Equal("1.0 GiB", settings.ExecuteScalar());
        Assert.Equal(
            2,
            database.Store.ReadFolderEntries(id, Root, cancellationToken: Token).Entries.Length
        );
    }

    private static BufferedScan Buffer(string volume = "synthetic:A")
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
        buffer.Complete(Token);
        return buffer;
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
