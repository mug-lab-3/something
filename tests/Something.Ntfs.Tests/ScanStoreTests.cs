using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Something.Application.Browsing;
using Something.Application.Scanning;
using Something.Ntfs;
using Something.Statistics;
using Something.Storage;

namespace Something.Ntfs.Tests;

public sealed class ScanStoreTests
{
    [Fact]
    public void StandardAttributesRoundTripForFoldersFilesAndUnknownWithoutNameFallback()
    {
        using var fixture = new DatabaseFixture();
        var token = TestContext.Current.CancellationToken;
        var id = fixture.Store.BeginScan(Descriptor(), token);
        fixture.Store.StageBatch(
            id,
            [
                Stage(Directory(5, Root)),
                Stage(Directory(20, Root) with { StandardFileAttributes = 6 }),
                Stage(File(100, 12, Ref(20)) with { StandardFileAttributes = uint.MaxValue }),
                Stage(File(101, 10, Ref(20))),
            ],
            token
        );
        fixture.Store.CompleteScan(id, Started.AddSeconds(1), token);
        fixture.Reopen();

        var directory = Assert.Single(
            fixture.Store.ReadFolderEntries(id, Root, cancellationToken: token).Entries
        );
        Assert.True(directory.IsHidden);
        Assert.True(directory.IsSystem);
        var files = fixture.Store.ReadFolderEntries(id, Ref(20), cancellationToken: token).Entries;
        Assert.Equal(
            uint.MaxValue,
            files.Single(file => file.Reference == Ref(100)).StandardFileAttributes
        );
        Assert.Null(files.Single(file => file.Reference == Ref(101)).IsHidden);
        var recursive = fixture.Store.ReadAnalysisFiles(id, token).ToArray();
        Assert.Equal(
            files.OrderBy(file => file.Reference.RecordIndex),
            recursive.Select(file => file.Entry).OrderBy(file => file.Reference.RecordIndex)
        );
        Assert.All(recursive, file => Assert.Equal(Ref(20), file.Parent));
        Assert.Equal(
            6u,
            fixture
                .Store.ReadFiles(id, token)
                .Single(file => file.Reference == Ref(20))
                .StandardFileAttributes
        );
    }

    private static readonly DateTimeOffset Started = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly FileReference Root = Ref(5);

    private static FileReference Ref(ulong index, ushort sequence = 3) =>
        FileReference.Create(index, sequence);

    private static ScanDescriptor Descriptor(
        string volume = "synthetic-volume",
        MftScanRange? range = null
    ) =>
        new(
            volume,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ulong.MaxValue,
            range ?? new(0, 1000),
            Started
        );

    private static FileCapacityObservation File(
        ulong index,
        long bytes,
        params FileReference[] parents
    ) =>
        new(
            Ref(index),
            false,
            CapacityDomain.Ordinary,
            CapacityResolution.Resolved,
            [
                .. parents.Select(
                    (p, i) => new NtfsFileName(p, $"synthetic-{i}", FileNameNamespace.Win32, 0)
                ),
            ],
            [new("", bytes, bytes)],
            true
        );

    private static FileCapacityObservation Directory(ulong index, FileReference parent) =>
        File(index, 0, parent) with
        {
            IsDirectory = true,
        };

    private static StagedObservation Stage(FileCapacityObservation file) =>
        new(
            file.Reference.RecordIndex,
            file.Resolution == CapacityResolution.Resolved
                ? MftScanEntryKind.ResolvedFile
                : MftScanEntryKind.Unresolved,
            file.Resolution == CapacityResolution.Resolved
                ? LiveFileReadStatus.ObservedUnchanged
                : LiveFileReadStatus.Unsupported,
            file
        );

    private static StagedObservation[] BasicFacts(long size = 100) =>
        [
            Stage(Directory(5, Root)),
            Stage(Directory(20, Root)),
            Stage(Directory(21, Root)),
            Stage(File(100, size, Ref(20), Ref(21))),
        ];

    [Fact]
    public void StageRoundTrip_PreservesFullIdentityNamesAliasesStreamsAndUnknownCapacity()
    {
        using var fixture = new DatabaseFixture();
        var descriptor = Descriptor(range: new(0, FileReference.MaximumRecordIndex + 1));
        var id = fixture.Store.BeginScan(descriptor, TestContext.Current.CancellationToken);
        var file = File(FileReference.MaximumRecordIndex, long.MaxValue, Root) with
        {
            Reference = Ref(FileReference.MaximumRecordIndex, ushort.MaxValue),
            Names =
            [
                new(Root, "case", FileNameNamespace.Posix, uint.MaxValue),
                new(Root, "Case", FileNameNamespace.Win32, 0),
                new(Root, "CASE~1", FileNameNamespace.Dos, 0),
                new(Root, "unit-\uD800", FileNameNamespace.Posix, 0),
            ],
            Streams = [new("", long.MaxValue, 8192), new("named-\uDFFF", 0, 0)],
            ObservedHardLinkCount = ushort.MaxValue,
            LinkSetComplete = false,
        };
        var unknown = File(8, 0) with
        {
            Domain = CapacityDomain.NtfsMetadata,
            Resolution = CapacityResolution.Unresolved,
            Streams = [],
            LinkSetComplete = false,
        };
        fixture.Store.StageBatch(
            id,
            [Stage(file), Stage(unknown)],
            TestContext.Current.CancellationToken
        );
        var loaded = fixture.Store.ReadFiles(id, TestContext.Current.CancellationToken).ToArray();
        var roundtrip = loaded.Single(f => f.Reference == file.Reference);
        Assert.Equal(file.Names.ToArray(), roundtrip.Names.ToArray());
        Assert.Equal(file.Streams.ToArray(), roundtrip.Streams.ToArray());
        Assert.Equal(ushort.MaxValue, roundtrip.ObservedHardLinkCount);
        Assert.False(roundtrip.LinkSetComplete);
        Assert.Empty(loaded.Single(f => f.Reference.RecordIndex == 8).Streams);
        Assert.Equal(
            CapacityResolution.Unresolved,
            loaded.Single(f => f.Reference.RecordIndex == 8).Resolution
        );
        Assert.Equal(descriptor, fixture.Store.GetScan(id).Descriptor);
        Assert.Null(fixture.Store.GetPublishedScan(descriptor.VolumeKey, descriptor.Range));
    }

    [Fact]
    public void CompletedScan_PublishesSharedTotalsAndKeepsMetadataUnknownSeparate()
    {
        using var fixture = new DatabaseFixture();
        var descriptor = Descriptor();
        var id = fixture.Store.BeginScan(descriptor, TestContext.Current.CancellationToken);
        var bad = File(8, 0) with
        {
            Domain = CapacityDomain.NtfsMetadata,
            Resolution = CapacityResolution.Unresolved,
            Streams = [],
            LinkSetComplete = false,
        };
        fixture.Store.StageBatch(
            id,
            [.. BasicFacts(), Stage(bad)],
            TestContext.Current.CancellationToken
        );
        var result = fixture.Store.CompleteScan(
            id,
            Started.AddMinutes(1),
            TestContext.Current.CancellationToken
        );
        var restored = fixture.Store.ReadAggregation(id, TestContext.Current.CancellationToken);
        Assert.Equal(id, fixture.Store.GetPublishedScan(descriptor.VolumeKey, descriptor.Range));
        Assert.Equal(StoredScanState.Completed, fixture.Store.GetScan(id).State);
        Assert.Equal(new CapacityAmount(100, 100), result.Folders[Root].SharedData);
        Assert.Equal(1UL, result.Metadata.UnresolvedCount);
        Assert.False(result.HasUnknownAttribution);
        foreach (var pair in result.Folders)
            Assert.Equal(pair.Value, restored.Folders[pair.Key]);
        Assert.Equal(result.Metadata, restored.Metadata);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Store.StageBatch(id, [], TestContext.Current.CancellationToken)
        );
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Store.PruneDetailsBatch(id, TestContext.Current.CancellationToken)
        );
    }

    [Theory]
    [InlineData(StoredScanState.Cancelled)]
    [InlineData(StoredScanState.Failed)]
    [InlineData(StoredScanState.Interrupted)]
    public void StoppedOrFailedNewGeneration_PreservesPreviousPublishedResult(StoredScanState state)
    {
        using var fixture = new DatabaseFixture();
        var descriptor = Descriptor();
        var first = fixture.Store.BeginScan(descriptor, TestContext.Current.CancellationToken);
        fixture.Store.StageBatch(first, BasicFacts(), TestContext.Current.CancellationToken);
        fixture.Store.CompleteScan(
            first,
            Started.AddSeconds(1),
            TestContext.Current.CancellationToken
        );
        var next = fixture.Store.BeginScan(descriptor, TestContext.Current.CancellationToken);
        fixture.Store.StageBatch(
            next,
            [Stage(File(100, 1, Ref(20)))],
            TestContext.Current.CancellationToken
        );
        Assert.Throws<InvalidDataException>(() =>
            fixture.Store.CompleteScan(
                next,
                Started.AddSeconds(2),
                TestContext.Current.CancellationToken
            )
        );
        fixture.Store.StopScan(
            next,
            state,
            Started.AddSeconds(2),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(first, fixture.Store.GetPublishedScan(descriptor.VolumeKey, descriptor.Range));
        Assert.Equal(state, fixture.Store.GetScan(next).State);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Store.ReadAggregation(next, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public void DuplicateInBatch_RollsBackWholeBatchAndPreservesPriorStaging()
    {
        using var fixture = new DatabaseFixture();
        var id = fixture.Store.BeginScan(Descriptor(), TestContext.Current.CancellationToken);
        fixture.Store.StageBatch(
            id,
            [Stage(Directory(5, Root))],
            TestContext.Current.CancellationToken
        );
        Assert.Throws<InvalidDataException>(() =>
            fixture.Store.StageBatch(
                id,
                [Stage(File(100, 1, Root)), Stage(File(100, 2, Root))],
                TestContext.Current.CancellationToken
            )
        );
        Assert.Single(fixture.Store.ReadFiles(id, TestContext.Current.CancellationToken));
        fixture.Store.StageBatch(
            id,
            [Stage(File(100, 3, Root))],
            TestContext.Current.CancellationToken
        );
        Assert.Equal(2, fixture.Store.ReadFiles(id, TestContext.Current.CancellationToken).Count());
    }

    [Fact]
    public void InvalidBatchContract_DoesNotCommitEarlierRows()
    {
        using var fixture = new DatabaseFixture();
        var id = fixture.Store.BeginScan(Descriptor(), TestContext.Current.CancellationToken);
        var partial = File(101, 10, Root) with { Resolution = CapacityResolution.Unresolved };
        Assert.Throws<InvalidDataException>(() =>
            fixture.Store.StageBatch(
                id,
                [Stage(File(100, 1, Root)), Stage(partial)],
                TestContext.Current.CancellationToken
            )
        );
        Assert.Empty(fixture.Store.ReadFiles(id, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() =>
            fixture.Store.StageBatch(
                id,
                [Stage(File(1000, 1, Root))],
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public void UnknownIdentityIssue_IsNotInventedAsSequenceZeroOrSilentlyDropped()
    {
        using var fixture = new DatabaseFixture();
        var id = fixture.Store.BeginScan(Descriptor(), TestContext.Current.CancellationToken);
        fixture.Store.StageBatch(
            id,
            [
                .. BasicFacts(),
                new(99, MftScanEntryKind.Unresolved, LiveFileReadStatus.Missing, null),
            ],
            TestContext.Current.CancellationToken
        );
        var result = fixture.Store.CompleteScan(
            id,
            Started.AddSeconds(1),
            TestContext.Current.CancellationToken
        );
        Assert.True(result.HasUnknownAttribution);
        Assert.DoesNotContain(
            fixture.Store.ReadFiles(id, TestContext.Current.CancellationToken),
            f => f.Reference.RecordIndex == 99
        );
        Assert.Equal(new CapacityAmount(100, 100), result.Folders[Root].SubtreeData);
    }

    [Fact]
    public void PruningSupersededDetails_PreservesFolderNamesAndAggregatedHistory()
    {
        using var fixture = new DatabaseFixture();
        var descriptor = Descriptor();
        var first = fixture.Store.BeginScan(descriptor, TestContext.Current.CancellationToken);
        fixture.Store.StageBatch(first, BasicFacts(), TestContext.Current.CancellationToken);
        fixture.Store.CompleteScan(
            first,
            Started.AddSeconds(1),
            TestContext.Current.CancellationToken
        );
        var expectedNames = fixture.Store.ReadHistoricalFolderNames(
            first,
            Ref(20),
            TestContext.Current.CancellationToken
        );
        var next = fixture.Store.BeginScan(descriptor, TestContext.Current.CancellationToken);
        fixture.Store.StageBatch(next, BasicFacts(150), TestContext.Current.CancellationToken);
        fixture.Store.CompleteScan(
            next,
            Started.AddSeconds(2),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(
            4,
            fixture.Store.PruneDetailsBatch(first, TestContext.Current.CancellationToken)
        );
        Assert.Empty(fixture.Store.ReadFiles(first, TestContext.Current.CancellationToken));
        Assert.Equal(
            new CapacityAmount(100, 100),
            fixture
                .Store.ReadAggregation(first, TestContext.Current.CancellationToken)
                .Folders[Root]
                .SubtreeData
        );
        Assert.Equal(
            expectedNames.ToArray(),
            fixture
                .Store.ReadHistoricalFolderNames(
                    first,
                    Ref(20),
                    TestContext.Current.CancellationToken
                )
                .ToArray()
        );
        Assert.Empty(
            fixture.Store.ReadHistoricalFolderNames(
                first,
                Ref(20, 4),
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(next, fixture.Store.GetPublishedScan(descriptor.VolumeKey, descriptor.Range));
    }

    [Fact]
    public void AggregateBeyondSigned64Bit_RoundTripsWithoutOverflowOrTruncation()
    {
        using var fixture = new DatabaseFixture();
        var id = fixture.Store.BeginScan(Descriptor(), TestContext.Current.CancellationToken);
        fixture.Store.StageBatch(
            id,
            [
                Stage(Directory(5, Root)),
                Stage(File(100, long.MaxValue, Root)),
                Stage(File(101, long.MaxValue, Root)),
            ],
            TestContext.Current.CancellationToken
        );
        fixture.Store.CompleteScan(
            id,
            Started.AddSeconds(1),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(
            (UInt128)long.MaxValue * 2,
            fixture
                .Store.ReadAggregation(id, TestContext.Current.CancellationToken)
                .Folders[Root]
                .SubtreeData.AllocatedBytes
        );
    }

    [Fact]
    public void Reopen_DoesNotPublishRunningGenerationAndKeepsCompletedSnapshot()
    {
        using var fixture = new DatabaseFixture();
        var descriptor = Descriptor();
        var first = fixture.Store.BeginScan(descriptor, TestContext.Current.CancellationToken);
        fixture.Store.StageBatch(first, BasicFacts(), TestContext.Current.CancellationToken);
        fixture.Store.CompleteScan(
            first,
            Started.AddSeconds(1),
            TestContext.Current.CancellationToken
        );
        var next = fixture.Store.BeginScan(descriptor, TestContext.Current.CancellationToken);
        fixture.Reopen();
        Assert.Equal(first, fixture.Store.GetPublishedScan(descriptor.VolumeKey, descriptor.Range));
        Assert.Equal(StoredScanState.Running, fixture.Store.GetScan(next).State);
        fixture.Store.StopScan(
            next,
            StoredScanState.Interrupted,
            Started.AddSeconds(2),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(first, fixture.Store.GetPublishedScan(descriptor.VolumeKey, descriptor.Range));
    }

    [Fact]
    public void OneRunningScanConstraint_AndIndependentReader_KeepExistingGeneration()
    {
        using var fixture = new DatabaseFixture();
        var id = fixture.Store.BeginScan(Descriptor(), TestContext.Current.CancellationToken);
        Assert.Throws<DbUpdateException>(() =>
            fixture.Store.BeginScan(Descriptor(), TestContext.Current.CancellationToken)
        );
        using var other = ScanStore.OpenTrustedPath(
            fixture.DatabasePath,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(StoredScanState.Running, other.GetScan(id).State);
        fixture.Store.StageBatch(id, BasicFacts(), TestContext.Current.CancellationToken);
        fixture.Store.CompleteScan(
            id,
            Started.AddSeconds(1),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(id, other.GetPublishedScan(Descriptor().VolumeKey, Descriptor().Range));
    }

    [Fact]
    public void CancelledPublication_PreservesOldPointerAndStagedFacts()
    {
        using var fixture = new DatabaseFixture();
        var descriptor = Descriptor();
        var first = fixture.Store.BeginScan(descriptor, TestContext.Current.CancellationToken);
        fixture.Store.StageBatch(first, BasicFacts(), TestContext.Current.CancellationToken);
        fixture.Store.CompleteScan(
            first,
            Started.AddSeconds(1),
            TestContext.Current.CancellationToken
        );
        var next = fixture.Store.BeginScan(descriptor, TestContext.Current.CancellationToken);
        fixture.Store.StageBatch(next, BasicFacts(150), TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            fixture.Store.CompleteScan(next, Started.AddSeconds(2), cancellation.Token)
        );
        Assert.Throws<OperationCanceledException>(() =>
            fixture.Store.StageBatch(next, [], cancellation.Token)
        );
        Assert.Equal(first, fixture.Store.GetPublishedScan(descriptor.VolumeKey, descriptor.Range));
        Assert.Equal(StoredScanState.Running, fixture.Store.GetScan(next).State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ForeignNewerOrCorruptDatabase_IsRejectedWithoutDeletingOriginal(int scenario)
    {
        using var fixture = new DatabaseFixture();
        fixture.Store.Dispose();
        if (scenario == 2)
            System.IO.File.WriteAllBytes(fixture.DatabasePath, [1, 2, 3, 4]);
        else
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = fixture.DatabasePath,
                    Pooling = false,
                }.ToString()
            );
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                scenario == 0 ? "PRAGMA application_id=42" : "PRAGMA user_version=999";
            command.ExecuteNonQuery();
        }
        byte[] before = System.IO.File.ReadAllBytes(fixture.DatabasePath);
        Assert.ThrowsAny<Exception>(() =>
            ScanStore.OpenTrustedPath(fixture.DatabasePath, TestContext.Current.CancellationToken)
        );
        Assert.Equal(before, System.IO.File.ReadAllBytes(fixture.DatabasePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DescriptorOrTimeContract_InvalidInput_IsRejected(int scenario)
    {
        using var fixture = new DatabaseFixture();
        var descriptor = Descriptor() with
        {
            VolumeKey = scenario == 0 ? "" : "synthetic-volume",
            Epoch = scenario == 1 ? Guid.Empty : Descriptor().Epoch,
            Range = scenario == 2 ? default : Descriptor().Range,
            StartedUtc = scenario == 3 ? Started.ToOffset(TimeSpan.FromHours(9)) : Started,
        };
        Assert.Throws<ArgumentException>(() =>
            fixture.Store.BeginScan(descriptor, TestContext.Current.CancellationToken)
        );
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SyntheticLiveScan_AdapterStageAndAggregation_WorkWithoutVolumeAccess(
        bool dosAlias,
        bool headerMismatch
    )
    {
        byte[] root = RecordFixture.Record(
            [
                RecordFixture.Resident(
                    NtfsAttributeTypes.FileName,
                    0,
                    RecordFixture.FileName("synthetic-root", parent: Root.Value)
                ),
            ],
            flags: MftRecordFlags.InUse | MftRecordFlags.Directory,
            index: 5
        );
        byte[] file = RecordFixture.Record(
            [
                RecordFixture.Resident(
                    NtfsAttributeTypes.FileName,
                    0,
                    RecordFixture.FileName(
                        "synthetic.bin",
                        FileNameNamespace.Win32AndDos,
                        Root.Value
                    )
                ),
                RecordFixture.Resident(NtfsAttributeTypes.Data, 1, new byte[11]),
            ],
            index: 42
        );
        if (dosAlias)
        {
            file = RecordFixture.Record(
                [
                    RecordFixture.Resident(
                        NtfsAttributeTypes.FileName,
                        0,
                        RecordFixture.FileName("synthetic.bin", parent: Root.Value)
                    ),
                    RecordFixture.Resident(
                        NtfsAttributeTypes.FileName,
                        1,
                        RecordFixture.FileName("SYNTHE~1.BIN", FileNameNamespace.Dos, Root.Value)
                    ),
                    RecordFixture.Resident(NtfsAttributeTypes.Data, 2, new byte[11]),
                ],
                index: 42
            );
            RecordFixture.U16(file, 18, 2);
        }
        ushort headerCount =
            headerMismatch ? (ushort)3
            : dosAlias ? (ushort)2
            : (ushort)1;
        RecordFixture.U16(file, 18, headerCount);
        using var fixture = new DatabaseFixture();
        var id = fixture.Store.BeginScan(Descriptor(), TestContext.Current.CancellationToken);
        await foreach (
            var entry in new LiveMftScanner(4096).ScanAsync(
                new SyntheticSource(root, file),
                new(0, 1000),
                TestContext.Current.CancellationToken
            )
        )
            fixture.Store.StageBatch(
                id,
                [StagedObservation.FromEntry(entry, TestContext.Current.CancellationToken)],
                TestContext.Current.CancellationToken
            );
        var result = fixture.Store.CompleteScan(
            id,
            Started.AddSeconds(1),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(new CapacityAmount(11, 0), result.Folders[Root].SubtreeData);
        Assert.Equal(default, result.Unattributed.KnownData);
        Assert.False(result.HasUnknownAttribution);
        var stored = fixture
            .Store.ReadFiles(id, TestContext.Current.CancellationToken)
            .Single(f => f.Reference.RecordIndex == 42);
        Assert.Equal("synthetic.bin", stored.Names[0].Name);
        Assert.Equal(dosAlias ? 2 : 1, stored.Names.Length);
        Assert.Equal<ushort?>(headerCount, stored.ObservedHardLinkCount);
        Assert.True(stored.LinkSetComplete);
    }

    [Fact]
    public void Aggregation_ConservesKnownCapacityAcrossFoldersMetadataAndMissingNames()
    {
        using var fixture = new DatabaseFixture();
        var token = TestContext.Current.CancellationToken;
        var id = fixture.Store.BeginScan(Descriptor(), token);
        var metadata = File(0, 17, Root) with { Domain = CapacityDomain.NtfsMetadata };
        var missingName = File(101, 31, Root) with { LinkSetComplete = false };
        fixture.Store.StageBatch(
            id,
            [.. BasicFacts(100), Stage(metadata), Stage(missingName)],
            token
        );

        var result = fixture.Store.CompleteScan(id, Started.AddSeconds(1), token);
        var restored = fixture.Store.ReadAggregation(id, token);

        Assert.Equal(new CapacityAmount(100, 100), result.Folders[Root].SubtreeData);
        Assert.Equal(new CapacityAmount(17, 17), restored.Metadata.KnownData);
        Assert.Equal(new CapacityAmount(31, 31), restored.Unattributed.KnownData);
        Assert.Equal(1UL, restored.Unattributed.ResolvedCount);
        Assert.Equal(0UL, restored.Unattributed.UnresolvedCount);
        Assert.True(restored.HasUnknownAttribution);
        Assert.Equal(
            new CapacityAmount(148, 148),
            restored.Folders[Root].SubtreeData
                + restored.Metadata.KnownData
                + restored.Unattributed.KnownData
        );
    }

    [Fact]
    public void DatabaseConstraintFailure_RollsBackAndReleasesTrackedBatch()
    {
        using var fixture = new DatabaseFixture();
        var id = fixture.Store.BeginScan(Descriptor(), TestContext.Current.CancellationToken);
        fixture.Store.StageBatch(
            id,
            [Stage(Directory(5, Root))],
            TestContext.Current.CancellationToken
        );
        var duplicateStream = File(101, 1, Root) with { Streams = [new("", 1, 1), new("", 2, 2)] };
        Assert.Throws<DbUpdateException>(() =>
            fixture.Store.StageBatch(
                id,
                [Stage(File(100, 1, Root)), Stage(duplicateStream)],
                TestContext.Current.CancellationToken
            )
        );
        Assert.Single(fixture.Store.ReadFiles(id, TestContext.Current.CancellationToken));
        fixture.Store.StageBatch(
            id,
            [Stage(File(100, 3, Root))],
            TestContext.Current.CancellationToken
        );
        var result = fixture.Store.CompleteScan(
            id,
            Started.AddSeconds(1),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(new CapacityAmount(3, 3), result.Folders[Root].SubtreeData);
    }

    [Fact]
    public void MultiplePages_RoundTripEveryFileOnceAndPruneOnlyBoundedDetails()
    {
        using var fixture = new DatabaseFixture();
        var descriptor = Descriptor(range: new(0, 3000));
        var id = fixture.Store.BeginScan(descriptor, TestContext.Current.CancellationToken);
        var facts = Enumerable
            .Range(20, 1005)
            .Select(i => Stage(File((ulong)i, 1, Root)))
            .Prepend(Stage(Directory(5, Root)))
            .ToArray();
        foreach (var batch in facts.Chunk(ScanStore.MaximumBatchSize))
            fixture.Store.StageBatch(id, batch, TestContext.Current.CancellationToken);
        Assert.Equal(
            facts.Length,
            fixture
                .Store.ReadFiles(id, TestContext.Current.CancellationToken)
                .Select(f => f.Reference)
                .Distinct()
                .Count()
        );
        var result = fixture.Store.CompleteScan(
            id,
            Started.AddSeconds(1),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(new CapacityAmount(1005, 1005), result.Folders[Root].SubtreeData);
        var next = fixture.Store.BeginScan(descriptor, TestContext.Current.CancellationToken);
        fixture.Store.StageBatch(next, BasicFacts(), TestContext.Current.CancellationToken);
        fixture.Store.CompleteScan(
            next,
            Started.AddSeconds(2),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(
            1000,
            fixture.Store.PruneDetailsBatch(id, TestContext.Current.CancellationToken)
        );
        Assert.Equal(6, fixture.Store.PruneDetailsBatch(id, TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Store.PruneDetailsBatch(id, TestContext.Current.CancellationToken));
        Assert.Equal(
            result.Folders[Root],
            fixture.Store.ReadAggregation(id, TestContext.Current.CancellationToken).Folders[Root]
        );
    }

    [Fact]
    public void SparsePages_PreserveNamesAndStreamsAcrossRecordGapsAndBatchBoundaries()
    {
        using var fixture = new DatabaseFixture();
        var token = TestContext.Current.CancellationToken;
        var id = fixture.Store.BeginScan(Descriptor(range: new(0, 30_000)), token);
        var files = Enumerable
            .Range(1, 1005)
            .Select(i =>
                File((ulong)i * 20 + 16, i, Root) with
                {
                    Names =
                    [
                        new(Root, $"long-{i}", FileNameNamespace.Win32, 0),
                        new(Root, $"DOS-{i}", FileNameNamespace.Dos, uint.MaxValue),
                    ],
                    Streams = [new("", i, (long)i * 4096), new($"named-{i}", i + 1, 0)],
                    ObservedHardLinkCount = 2,
                }
            )
            .ToArray();
        foreach (
            var batch in files
                .Select(Stage)
                .Prepend(Stage(Directory(5, Root)))
                .Chunk(ScanStore.MaximumBatchSize)
        )
            fixture.Store.StageBatch(id, batch, token);

        fixture.Reopen();
        var loaded = fixture.Store.ReadFiles(id, token).Where(f => !f.IsDirectory).ToArray();
        var result = fixture.Store.CompleteScan(id, Started.AddSeconds(1), token);

        Assert.Equal(files.Length, loaded.Length);
        for (int i = 0; i < files.Length; i++)
        {
            Assert.Equal(files[i].Reference, loaded[i].Reference);
            Assert.Equal(files[i].Names.ToArray(), loaded[i].Names.ToArray());
            Assert.Equal(files[i].Streams.ToArray(), loaded[i].Streams.ToArray());
        }
        Assert.Equal(
            new CapacityAmount(1_012_035, 505_515UL * 4096),
            result.Folders[Root].SubtreeData
        );
        Assert.Equal(default, result.Unattributed.KnownData);
    }

    [Fact]
    public void BufferedPublication_MatchesStagingAndPreservesHistoryFactsAndUnknownIssues()
    {
        using var staged = new DatabaseFixture();
        using var buffered = new DatabaseFixture();
        var token = TestContext.Current.CancellationToken;
        var descriptor = Descriptor();
        var shared = File(100, 110, Ref(20), Ref(21)) with
        {
            Names =
            [
                new(Ref(20), "long-\uD800", FileNameNamespace.Win32, uint.MaxValue),
                new(Ref(20), "DOS~1", FileNameNamespace.Dos, 0),
                new(Ref(21), "another", FileNameNamespace.Win32, 0),
            ],
            Streams = [new("", 110, 8192), new("named-\uDFFF", 9, 0)],
            ObservedHardLinkCount = 3,
        };
        var unknown = File(8, 0) with
        {
            Domain = CapacityDomain.NtfsMetadata,
            Resolution = CapacityResolution.Unresolved,
            Streams = [],
            LinkSetComplete = false,
        };
        StagedObservation[] facts =
        [
            .. BasicFacts().Where(o => o.RecordIndex != 100),
            Stage(shared),
            Stage(unknown),
            new(101, MftScanEntryKind.Unresolved, LiveFileReadStatus.InvalidRecord, null),
            new(102, MftScanEntryKind.ExtensionRecord, LiveFileReadStatus.ObservedUnchanged, null),
        ];
        var stagedId = staged.Store.BeginScan(descriptor, token);
        staged.Store.StageBatch(stagedId, facts, token);
        var expected = staged.Store.CompleteScan(stagedId, Started.AddMinutes(1), token);
        var buffer = new BufferedScan(descriptor);
        Assert.Throws<InvalidOperationException>(() =>
            buffer.ReadAccountingDiagnostics(Root, cancellationToken: token)
        );
        foreach (var fact in facts.Reverse())
            buffer.Add(fact, token);
        Assert.Throws<InvalidOperationException>(() =>
            buffered.Store.PublishBufferedScan(buffer, Started.AddMinutes(1), token)
        );
        var actual = buffer.Complete(token);
        var bufferedId = buffered.Store.PublishBufferedScan(buffer, Started.AddMinutes(1), token);

        Assert.Equal(facts.Length, buffer.Count);
        Assert.Same(actual, buffer.Complete(token));
        Assert.Equal(expected.Metadata, actual.Metadata);
        Assert.Equal(expected.Unattributed, actual.Unattributed);
        Assert.Equal(expected.HasUnknownAttribution, actual.HasUnknownAttribution);
        foreach (var pair in expected.Folders)
        {
            Assert.Equal(pair.Value, actual.Folders[pair.Key]);
            Assert.Equal(
                pair.Value.SubtreeData,
                buffer.ReadAccountingDiagnostics(pair.Key, cancellationToken: token).AssignedData
            );
        }
        var stored = buffered.Store.ReadFiles(bufferedId, token).ToDictionary(f => f.Reference);
        foreach (var file in staged.Store.ReadFiles(stagedId, token))
        {
            Assert.Equal(
                file with
                {
                    Names = [],
                    Streams = [],
                },
                stored[file.Reference] with
                {
                    Names = [],
                    Streams = [],
                }
            );
            Assert.Equal(file.Names.ToArray(), stored[file.Reference].Names.ToArray());
            Assert.Equal(file.Streams.ToArray(), stored[file.Reference].Streams.ToArray());
        }
        Assert.Equal(
            bufferedId,
            buffered.Store.GetPublishedScan(descriptor.VolumeKey, descriptor.Range)
        );
        Assert.Equal(Started.AddMinutes(1), buffered.Store.GetScan(bufferedId).EndedUtc);
        using var inspection = new ScanDbContext(
            new DbContextOptionsBuilder<ScanDbContext>()
                .UseSqlite($"Data Source={buffered.DatabasePath};Pooling=False")
                .Options
        );
        var issues = inspection
            .Observations.AsNoTracking()
            .Where(o => o.ScanId == bufferedId && o.Sequence == null)
            .ToArray();
        Assert.Contains(
            issues,
            o =>
                o.RecordIndex == 101
                && o.Kind == MftScanEntryKind.Unresolved
                && o.Status == LiveFileReadStatus.InvalidRecord
        );
        Assert.Contains(
            issues,
            o => o.RecordIndex == 102 && o.Kind == MftScanEntryKind.ExtensionRecord
        );
        Assert.Throws<InvalidOperationException>(() => buffer.Add(facts[0], token));
        foreach (var folder in expected.Folders.Keys)
            Assert.Equal(
                staged.Store.ReadHistoricalFolderNames(stagedId, folder, token).ToArray(),
                buffered.Store.ReadHistoricalFolderNames(bufferedId, folder, token).ToArray()
            );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BufferedPublication_FailureOrCancellationLeavesNoNewGeneration(bool cancel)
    {
        using var fixture = new DatabaseFixture();
        var token = TestContext.Current.CancellationToken;
        var descriptor = Descriptor();
        var old = fixture.Store.BeginScan(descriptor, token);
        fixture.Store.StageBatch(old, BasicFacts(), token);
        fixture.Store.CompleteScan(old, Started.AddSeconds(1), token);
        var buffer = new BufferedScan(descriptor);
        foreach (var fact in BasicFacts(200))
            buffer.Add(fact, token);
        buffer.Complete(token);
        if (cancel)
        {
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                fixture.Store.PublishBufferedScan(buffer, Started.AddSeconds(2), cancelled.Token)
            );
        }
        else
        {
            using var connection = new SqliteConnection(
                $"Data Source={fixture.DatabasePath};Pooling=False"
            );
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TRIGGER reject_test_file BEFORE INSERT ON file_name WHEN NEW.RecordIndex = 100 BEGIN SELECT RAISE(ABORT, 'test failure'); END;";
            command.ExecuteNonQuery();
            Assert.Throws<DbUpdateException>(() =>
                fixture.Store.PublishBufferedScan(buffer, Started.AddSeconds(2), token)
            );
            command.CommandText = "DROP TRIGGER reject_test_file";
            command.ExecuteNonQuery();
        }
        using var inspection = new ScanDbContext(
            new DbContextOptionsBuilder<ScanDbContext>()
                .UseSqlite($"Data Source={fixture.DatabasePath};Pooling=False")
                .Options
        );
        Assert.Single(inspection.Scans.AsNoTracking());
        Assert.Equal(old, fixture.Store.GetPublishedScan(descriptor.VolumeKey, descriptor.Range));
        Assert.Equal(4, inspection.Observations.Count());
        var retry = fixture.Store.PublishBufferedScan(buffer, Started.AddSeconds(2), token);
        Assert.Equal(retry, fixture.Store.GetPublishedScan(descriptor.VolumeKey, descriptor.Range));
        Assert.Equal(
            new CapacityAmount(200, 200),
            fixture.Store.ReadAggregation(retry, token).Folders[Root].SubtreeData
        );
    }

    [Fact]
    public void BufferedScan_RejectsDuplicatesInvalidContractsAndIncompleteHierarchy()
    {
        var token = TestContext.Current.CancellationToken;
        var buffer = new BufferedScan(Descriptor());
        var file = Stage(File(100, 1, Root));
        buffer.Add(file, token);
        Assert.Throws<InvalidDataException>(() => buffer.Add(file, token));
        Assert.Throws<InvalidDataException>(() => buffer.Complete(token));
        Assert.Throws<ArgumentException>(() => buffer.Add(file with { RecordIndex = 1001 }, token));
        buffer.Add(Stage(Directory(5, Root)), token);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => buffer.Complete(cancelled.Token));
        Assert.Equal(new CapacityAmount(1, 1), buffer.Complete(token).Folders[Root].SubtreeData);
    }

    [Theory]
    [InlineData(0UL, 1000UL)]
    [InlineData(7UL, 130UL)]
    [InlineData(0UL, BufferedScan.BitmapRecordLimit + 1)]
    public void BufferedScan_RejectsDuplicatesAtRangeEdgesWithBitmapOrLargeRangeFallback(
        ulong start,
        ulong count
    )
    {
        var token = TestContext.Current.CancellationToken;
        var buffer = new BufferedScan(Descriptor(range: new(start, count)));
        ulong last = start + count - 1;
        static StagedObservation Extension(ulong index) =>
            new(
                index,
                MftScanEntryKind.ExtensionRecord,
                LiveFileReadStatus.ObservedUnchanged,
                null
            );
        foreach (ulong index in new[] { start, start + 63, start + 64, last }.Distinct())
        {
            buffer.Add(Extension(index), token);
            Assert.Throws<InvalidDataException>(() => buffer.Add(Extension(index), token));
        }
        Assert.Throws<ArgumentException>(() => buffer.Add(Extension(last + 1), token));
        Assert.Equal(
            new[] { start, start + 63, start + 64, last }.Distinct().Count(),
            buffer.Count
        );
    }

    [Fact]
    public void PreparedInserts_PreserveConversionsNullTransitionsAndConstraintErrors()
    {
        using var fixture = new DatabaseFixture();
        var token = TestContext.Current.CancellationToken;
        var first = fixture.Store.BeginScan(Descriptor(), token);
        var second = fixture.Store.BeginScan(Descriptor("another-synthetic-volume"), token);
        using var context = new ScanDbContext(
            new DbContextOptionsBuilder<ScanDbContext>()
                .UseSqlite($"Data Source={fixture.DatabasePath};Pooling=False;Foreign Keys=True")
                .Options
        );
        Assert.Throws<InvalidOperationException>(() => new ModelInsert<ObservationEntity>(context));
        using var transaction = context.Database.BeginTransaction();
        using var rows = new ModelInsert<ObservationEntity>(context);
        rows.Add(
            new()
            {
                ScanId = first,
                RecordIndex = 100,
                Kind = MftScanEntryKind.ResolvedFile,
                Status = LiveFileReadStatus.ObservedUnchanged,
                Sequence = ushort.MaxValue,
                ObservedLinkCount = ushort.MaxValue,
                IsDirectory = false,
                LinksComplete = true,
                Domain = CapacityDomain.Ordinary,
                Resolution = CapacityResolution.Resolved,
            },
            token
        );
        rows.Add(
            new()
            {
                ScanId = second,
                RecordIndex = 100,
                Kind = MftScanEntryKind.Unresolved,
                Status = LiveFileReadStatus.InvalidRecord,
            },
            token
        );
        rows.Add(
            new()
            {
                ScanId = first,
                RecordIndex = 101,
                Kind = MftScanEntryKind.Unresolved,
                Status = LiveFileReadStatus.Unsupported,
                Sequence = 1,
                IsDirectory = true,
                LinksComplete = false,
                Domain = CapacityDomain.Ordinary,
                Resolution = CapacityResolution.Unresolved,
            },
            token
        );
        using var names = new ModelInsert<FileNameEntity>(context);
        string unusualName = "日本語😀'\"\0" + new string('界', 300) + "\uD800";
        names.Add(
            new()
            {
                ScanId = first,
                RecordIndex = 100,
                Name = unusualName,
                Namespace = FileNameNamespace.Posix,
                ParentIndex = 5,
                ParentSequence = ushort.MaxValue,
                Attributes = uint.MaxValue,
            },
            token
        );
        using var streams = new ModelInsert<StreamEntity>(context);
        streams.Add(
            new()
            {
                ScanId = first,
                RecordIndex = 100,
                Name = "",
                Logical = long.MaxValue,
                Allocated = 0,
            },
            token
        );
        using var folders = new ModelInsert<FolderSampleEntity>(context);
        folders.Add(
            new()
            {
                ScanId = first,
                RecordIndex = 5,
                Sequence = 1,
                SubtreeLogical = UInt128.MaxValue,
            },
            token
        );
        var duplicate = Assert.Throws<DbUpdateException>(() =>
            rows.Add(new() { ScanId = first, RecordIndex = 100 }, token)
        );
        Assert.Equal(19, Assert.IsType<SqliteException>(duplicate.InnerException).SqliteErrorCode);
        var foreignKey = Assert.Throws<DbUpdateException>(() =>
            rows.Add(new() { ScanId = Guid.NewGuid(), RecordIndex = 100 }, token)
        );
        Assert.Equal(
            787,
            Assert.IsType<SqliteException>(foreignKey.InnerException).SqliteExtendedErrorCode
        );
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            rows.Add(new() { ScanId = first, RecordIndex = 102 }, cancelled.Token)
        );
        rows.Add(new() { ScanId = first, RecordIndex = 102 }, token);
        transaction.Commit();

        var loaded = context.Observations.AsNoTracking().OrderBy(o => o.RecordIndex).ToArray();
        Assert.Equal(4, loaded.Length);
        var valid = loaded.Single(o => o.ScanId == first && o.RecordIndex == 100);
        Assert.Equal(ushort.MaxValue, valid.Sequence);
        Assert.Equal(ushort.MaxValue, valid.ObservedLinkCount);
        Assert.False(valid.IsDirectory);
        Assert.True(valid.LinksComplete);
        var missing = loaded.Single(o => o.ScanId == second);
        Assert.Null(missing.Sequence);
        Assert.Null(missing.IsDirectory);
        Assert.Null(missing.LinksComplete);
        Assert.Null(missing.ObservedLinkCount);
        Assert.True(loaded.Single(o => o.RecordIndex == 101).IsDirectory);
        Assert.False(loaded.Single(o => o.RecordIndex == 101).LinksComplete);
        var name = context.Names.AsNoTracking().Single();
        Assert.Equal(unusualName, name.Name);
        Assert.Equal(uint.MaxValue, name.Attributes);
        Assert.Equal(ushort.MaxValue, name.ParentSequence);
        Assert.Equal("", context.Streams.AsNoTracking().Single().Name);
        Assert.Equal(long.MaxValue, context.Streams.AsNoTracking().Single().Logical);
        var folder = context.Folders.AsNoTracking().Single();
        Assert.Equal(UInt128.MaxValue, folder.SubtreeLogical);
        Assert.Null(folder.EntrySubtreeLogical);
    }

    [Fact]
    public void BufferedDiagnostics_AccountForEveryUnattributedFileWithoutChangingSavedFacts()
    {
        using var fixture = new DatabaseFixture();
        var token = TestContext.Current.CancellationToken;
        var buffer = new BufferedScan(Descriptor());
        var incomplete = File(100, 71, Root) with
        {
            Names =
            [
                new(Root, "long", FileNameNamespace.Win32, 0),
                new(Root, "DOS~1", FileNameNamespace.Dos, 0),
            ],
            ObservedHardLinkCount = 1,
            LinkSetComplete = false,
        };
        Assert.Throws<InvalidOperationException>(() => buffer.Diagnostics);
        buffer.Add(Stage(Directory(5, Root)), token);
        buffer.Add(Stage(incomplete), token);
        buffer.Add(Stage(File(101, 23, Ref(999))), token);
        var result = buffer.Complete(token);
        var id = fixture.Store.PublishBufferedScan(buffer, Started.AddSeconds(1), token);
        Assert.Equal(new CapacityAmount(94, 94), result.Unattributed.KnownData);
        Assert.Equal(default, result.Folders[Root].SubtreeData);
        Assert.Equal(2, buffer.Diagnostics.Length);
        Assert.Equal(UnattributedReason.IncompleteLinks, buffer.Diagnostics[0].Reason);
        Assert.Equal(new CapacityAmount(71, 71), buffer.Diagnostics[0].KnownData);
        Assert.Equal(UnattributedReason.MissingParent, buffer.Diagnostics[1].Reason);
        Assert.Equal(Ref(999), buffer.Diagnostics[1].Samples[0].ProblemParent);
        Assert.Equal(result.Unattributed, fixture.Store.ReadAggregation(id, token).Unattributed);
        var stored = fixture
            .Store.ReadFiles(id, token)
            .Single(f => f.Reference == incomplete.Reference);
        Assert.Equal(incomplete.Names.ToArray(), stored.Names.ToArray());
        Assert.Equal(incomplete.Streams.ToArray(), stored.Streams.ToArray());
        Assert.False(stored.LinkSetComplete);
    }

    [Fact]
    public async Task EntryMigration_PreservesHistoricalTotalsWithoutInventingNameView()
    {
        using var fixture = new DatabaseFixture();
        var token = TestContext.Current.CancellationToken;
        var id = fixture.Store.BeginScan(Descriptor(), token);
        fixture.Store.StageBatch(id, BasicFacts(), token);
        var original = fixture.Store.CompleteScan(id, Started.AddSeconds(1), token);
        fixture.Store.Dispose();
        // Downgrade only this owned test database to exercise upgrade from the real old schema.
        await using (
            var context = new ScanDbContext(
                new DbContextOptionsBuilder<ScanDbContext>()
                    .UseSqlite(
                        new SqliteConnectionStringBuilder
                        {
                            DataSource = fixture.DatabasePath,
                            Pooling = false,
                            ForeignKeys = true,
                        }.ToString()
                    )
                    .Options
            )
        )
            await context
                .GetService<IMigrator>()
                .MigrateAsync("20261012155458_InitialScanStorage", token);

        fixture.Reopen();

        var restored = fixture.Store.ReadAggregation(id, token);
        foreach (var pair in original.Folders)
            Assert.Equal(pair.Value with { Entries = null }, restored.Folders[pair.Key]);
        Assert.Equal(original.Metadata, restored.Metadata);
        Assert.Equal(original.Unattributed, restored.Unattributed);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Store.ReadLargestFolders(id, cancellationToken: token)
        );
        var physical = fixture.Store.ReadLargestFolders(
            id,
            cancellationToken: token,
            metric: FolderRankingMetric.SingleCountAllData
        );
        Assert.All(physical, row => Assert.Equal(row.Sample.SubtreeData, row.Data));
    }

    [Fact]
    public void InitialMigration_MatchesModelAndSurvivesReopen()
    {
        using var fixture = new DatabaseFixture();
        using var context = new ScanDbContextFactory().CreateDbContext([]);
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Equal(3, context.Database.GetMigrations().Count());
        fixture.Reopen();
        var id = fixture.Store.BeginScan(Descriptor(), TestContext.Current.CancellationToken);
        Assert.Equal(StoredScanState.Running, fixture.Store.GetScan(id).State);
    }

    [Fact]
    public void FolderAnalysis_IncludesEveryMatchingFolderBeyondFormerLimits()
    {
        using var fixture = new DatabaseFixture();
        var token = TestContext.Current.CancellationToken;
        var id = fixture.Store.BeginScan(Descriptor(range: new(0, 4000)), token);
        var facts = new List<StagedObservation> { Stage(Directory(5, Root)) };
        for (ulong index = 20; index < 620; index++)
        {
            facts.Add(
                Stage(
                    Directory(index, Root) with
                    {
                        Names = [new(Root, $"folder-{index}", FileNameNamespace.Win32, 0)],
                    }
                )
            );
            facts.Add(Stage(File(index + 2000, checked((long)(620 - index)), Ref(index))));
        }
        foreach (var batch in facts.Chunk(ScanStore.MaximumBatchSize))
            fixture.Store.StageBatch(id, batch, token);
        fixture.Store.CompleteScan(id, Started.AddSeconds(1), token);

        var rows = fixture.Store.ReadFolderAnalysis(id, new(Depth: 1, LeafOnly: true), token);

        Assert.Equal(600, rows.Length);
        Assert.Equal(Ref(20), rows[0].Sample.Reference);
        Assert.Equal(Ref(619), rows[^1].Sample.Reference);
        Assert.Equal(600, rows.Select(row => row.Sample.Reference).Distinct().Count());
        Assert.Empty(fixture.Store.ReadFolderAnalysis(id, new(Depth: 2), token));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            fixture.Store.ReadFolderAnalysis(id, new(), cancelled.Token)
        );
    }

    [Fact]
    public void LargestFolders_RanksSubtreesAndExcludesRootAndMetadata()
    {
        using var fixture = new DatabaseFixture();
        var id = fixture.Store.BeginScan(Descriptor(), TestContext.Current.CancellationToken);
        var facts = new List<StagedObservation>
        {
            Stage(Directory(5, Root)),
            Stage(Directory(11, Root)),
        };
        foreach (ulong index in Enumerable.Range(20, 6).Select(i => (ulong)i))
        {
            facts.Add(
                Stage(
                    Directory(index, Root) with
                    {
                        Names = [new(Root, $"folder-{index}", FileNameNamespace.Win32, 0)],
                    }
                )
            );
            facts.Add(Stage(File(index + 100, checked((long)(26 - index) * 100), Ref(index))));
        }
        facts.Add(
            Stage(
                Directory(26, Ref(20)) with
                {
                    Names = [new(Ref(20), "nested", FileNameNamespace.Win32AndDos, 0)],
                }
            )
        );
        facts.Add(Stage(File(200, 50, Ref(26))));
        facts.Add(Stage(File(201, 10000, Ref(11))));
        fixture.Store.StageBatch(id, facts, TestContext.Current.CancellationToken);
        fixture.Store.CompleteScan(
            id,
            Started.AddSeconds(1),
            TestContext.Current.CancellationToken
        );
        var top = fixture.Store.ReadLargestFolders(
            id,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Equal(
            new ulong[] { 20, 21, 22, 23, 24 },
            top.Select(t => t.Sample.Reference.RecordIndex)
        );
        Assert.Equal("folder-20", top[0].RelativePath);
        Assert.Equal(new CapacityAmount(650, 650), top[0].Sample.SubtreeData);
        var all = fixture.Store.ReadLargestFolders(id, 100, TestContext.Current.CancellationToken);
        Assert.Equal(
            "folder-20\\nested",
            all.Single(t => t.Sample.Reference.RecordIndex == 26).RelativePath
        );
        Assert.DoesNotContain(all, t => t.Sample.Reference.RecordIndex is 5 or 11);
        var depthTwo = fixture.Store.ReadLargestFolders(
            id,
            cancellationToken: TestContext.Current.CancellationToken,
            filter: new(Depth: 2)
        );
        Assert.Equal(Ref(26), Assert.Single(depthTwo).Sample.Reference);
        Assert.Equal(new CapacityAmount(50, 50), depthTwo[0].Data);
        var leaf = fixture.Store.ReadLargestFolders(
            id,
            100,
            TestContext.Current.CancellationToken,
            filter: new(LeafOnly: true)
        );
        Assert.DoesNotContain(leaf, row => row.Sample.Reference == Ref(20));
        Assert.Contains(leaf, row => row.Sample.Reference == Ref(26));
        var depthOneLeaves = fixture.Store.ReadLargestFolders(
            id,
            100,
            TestContext.Current.CancellationToken,
            filter: new(Depth: 1, LeafOnly: true)
        );
        Assert.Equal(
            new ulong[] { 21, 22, 23, 24, 25 },
            depthOneLeaves.Select(row => row.Sample.Reference.RecordIndex)
        );
        Assert.Empty(
            fixture.Store.ReadLargestFolders(
                id,
                cancellationToken: TestContext.Current.CancellationToken,
                filter: new(Depth: 100)
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fixture.Store.ReadLargestFolders(
                id,
                cancellationToken: TestContext.Current.CancellationToken,
                filter: new(Depth: 0)
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fixture.Store.ReadLargestFolders(id, 0, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public void LargestFolders_SharedDataCountsOnceAtCommonAncestorAndReportsUnknown()
    {
        using var fixture = new DatabaseFixture();
        var id = fixture.Store.BeginScan(Descriptor(), TestContext.Current.CancellationToken);
        var unknown = File(202, 0, Ref(20)) with
        {
            Resolution = CapacityResolution.Unresolved,
            Streams = [],
        };
        fixture.Store.StageBatch(
            id,
            [
                Stage(Directory(5, Root)),
                Stage(Directory(20, Root)),
                Stage(Directory(21, Ref(20))),
                Stage(Directory(22, Ref(20))),
                Stage(File(200, 100, Ref(21), Ref(22))),
                Stage(File(201, 10, Ref(21))),
                Stage(unknown),
            ],
            TestContext.Current.CancellationToken
        );
        fixture.Store.CompleteScan(
            id,
            Started.AddSeconds(1),
            TestContext.Current.CancellationToken
        );
        var top = fixture.Store.ReadLargestFolders(
            id,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Equal(3, top.Length);
        Assert.Equal(Ref(20), top[0].Sample.Reference);
        Assert.Equal(new CapacityAmount(100, 100), top[0].Sample.SharedData);
        Assert.Equal(new CapacityAmount(110, 110), top[0].Sample.SubtreeData);
        Assert.Equal(new CapacityAmount(210, 210), top[0].Data);
        Assert.Equal(3UL, top[0].Sample.Entries!.SubtreeFileCount);
        Assert.Equal(1UL, top[0].Sample.Entries!.SubtreeUnresolvedCount);
        Assert.Equal(1UL, top[0].Sample.SubtreeUnresolvedCount);
        Assert.Equal(
            new CapacityAmount(10, 10),
            top.Single(t => t.Sample.Reference == Ref(21)).Sample.SubtreeData
        );
        Assert.Equal(
            new CapacityAmount(110, 110),
            top.Single(t => t.Sample.Reference == Ref(21)).Data
        );
        var physical = fixture.Store.ReadLargestFolders(
            id,
            cancellationToken: TestContext.Current.CancellationToken,
            metric: FolderRankingMetric.SingleCountAllData
        );
        Assert.Equal(new CapacityAmount(110, 110), physical[0].Data);
        Assert.Equal(
            new CapacityAmount(10, 10),
            physical.Single(t => t.Sample.Reference == Ref(21)).Data
        );
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            fixture.Store.ReadLargestFolders(id, cancellationToken: cancel.Token)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemporaryScan_DoesNotTouchHistoryAndCanPublishRankAndDispose(bool inMemory)
    {
        using var history = new DatabaseFixture();
        var saved = history.Store.BeginScan(Descriptor(), TestContext.Current.CancellationToken);
        history.Store.StageBatch(saved, BasicFacts(), TestContext.Current.CancellationToken);
        history.Store.CompleteScan(
            saved,
            Started.AddSeconds(1),
            TestContext.Current.CancellationToken
        );
        using var temporary = TemporaryScanDatabase.CreateSqlite(
            TestContext.Current.CancellationToken,
            inMemory
        );
        var transient = temporary.Store.BeginScan(
            Descriptor(),
            TestContext.Current.CancellationToken
        );
        temporary.Store.StageBatch(transient, BasicFacts(), TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() =>
            temporary.Store.ReadLargestFolders(
                transient,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
        temporary.Store.CompleteScan(
            transient,
            Started.AddSeconds(1),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(
            2,
            temporary
                .Store.ReadLargestFolders(
                    transient,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Length
        );
        temporary.Dispose();
        temporary.Dispose();
        Assert.Equal(
            saved,
            history.Store.GetPublishedScan(Descriptor().VolumeKey, Descriptor().Range)
        );
    }

    [Fact]
    public void MemorySnapshot_WritesOnlyAfterCompletionAndPreservesAllFacts()
    {
        var token = TestContext.Current.CancellationToken;
        using var temporary = TemporaryScanDatabase.CreateSqlite(token, inMemory: true);
        string directory = Path.GetDirectoryName(temporary.SnapshotPath)!;
        Assert.True(temporary.IsInMemory);
        Assert.Empty(System.IO.Directory.EnumerateFiles(directory));
        Assert.Throws<InvalidOperationException>(() => temporary.OpenSnapshot(token));
        Assert.Throws<InvalidOperationException>(() => temporary.ExportSnapshot(token));
        var buffer = new BufferedScan(Descriptor());
        foreach (var fact in BasicFacts())
            buffer.Add(fact, token);
        var expected = buffer.Complete(token);
        var id = temporary.Store.PublishBufferedScan(buffer, Started.AddSeconds(1), token);
        Assert.Empty(System.IO.Directory.EnumerateFiles(directory));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => temporary.ExportSnapshot(cancelled.Token));
        Assert.False(System.IO.File.Exists(temporary.SnapshotPath));

        long length = temporary.ExportSnapshot(token);

        Assert.Equal(length, new FileInfo(temporary.SnapshotPath).Length);
        Assert.True(length > 0);
        using (var snapshot = temporary.OpenSnapshot(token))
        {
            Assert.Equal(id, snapshot.GetPublishedScan(Descriptor().VolumeKey, Descriptor().Range));
            var restored = snapshot.ReadAggregation(id, token);
            Assert.Equal(expected.Metadata, restored.Metadata);
            Assert.Equal(expected.Unattributed, restored.Unattributed);
            Assert.Equal(expected.HasUnknownAttribution, restored.HasUnknownAttribution);
            foreach (var pair in expected.Folders)
            {
                Assert.Equal(pair.Value, restored.Folders[pair.Key]);
                Assert.Equal(
                    temporary.Store.ReadHistoricalFolderNames(id, pair.Key, token).ToArray(),
                    snapshot.ReadHistoricalFolderNames(id, pair.Key, token).ToArray()
                );
            }
            var files = snapshot.ReadFiles(id, token).ToDictionary(f => f.Reference);
            foreach (var file in temporary.Store.ReadFiles(id, token))
            {
                Assert.Equal(
                    file with
                    {
                        Names = [],
                        Streams = [],
                    },
                    files[file.Reference] with
                    {
                        Names = [],
                        Streams = [],
                    }
                );
                Assert.Equal(file.Names.ToArray(), files[file.Reference].Names.ToArray());
                Assert.Equal(file.Streams.ToArray(), files[file.Reference].Streams.ToArray());
            }
            Assert.Equal(
                temporary.Store.ReadLargestFolders(id, cancellationToken: token).ToArray(),
                snapshot.ReadLargestFolders(id, cancellationToken: token).ToArray()
            );
        }
        // The snapshot is now durable and readable. A second export must not overwrite it.
        byte[] before = System.IO.File.ReadAllBytes(temporary.SnapshotPath);
        Assert.Throws<IOException>(() => temporary.ExportSnapshot(token));
        Assert.Equal(before, System.IO.File.ReadAllBytes(temporary.SnapshotPath));
        using (
            var inspection = new SqliteConnection(
                $"Data Source={temporary.SnapshotPath};Mode=ReadOnly;Pooling=False"
            )
        )
        {
            inspection.Open();
            using var command = inspection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            Assert.Equal("ok", command.ExecuteScalar());
            command.CommandText = "PRAGMA foreign_key_check";
            Assert.Null(command.ExecuteScalar());
        }
        temporary.Dispose();
        Assert.False(System.IO.Directory.Exists(directory));
        Assert.Throws<ObjectDisposedException>(() => temporary.ExportSnapshot(token));
    }

    [Fact]
    public void MemorySnapshot_RejectsRunningGenerationWithoutCreatingAFile()
    {
        var token = TestContext.Current.CancellationToken;
        using var temporary = TemporaryScanDatabase.CreateSqlite(token, inMemory: true);
        var id = temporary.Store.BeginScan(Descriptor(), token);
        temporary.Store.StageBatch(id, BasicFacts(), token);
        Assert.Throws<InvalidOperationException>(() => temporary.ExportSnapshot(token));
        Assert.False(System.IO.File.Exists(temporary.SnapshotPath));
        temporary.Store.CompleteScan(id, Started.AddSeconds(1), token);
        Assert.True(temporary.ExportSnapshot(token) > 0);
    }

    [Theory]
    [InlineData(0, "wal")]
    [InlineData(1, "delete")]
    [InlineData(2, "memory")]
    [InlineData(3, "delete")]
    public void DatabaseProfiles_KeepConstraintsAndDurabilityWhileIsolatingDiagnosticSettings(
        int profileValue,
        string journal
    )
    {
        using var fixture = new DatabaseFixture();
        var profile = (SqliteDatabaseProfile)profileValue;
        string path = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "profile.sqlite3");
        using var context = SqliteDatabaseBootstrap.Open(
            path,
            TestContext.Current.CancellationToken,
            profile
        );
        object? Pragma(string name)
        {
            using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"PRAGMA {name}";
            return command.ExecuteScalar();
        }

        Assert.Equal(journal, Pragma("journal_mode"));
        Assert.Equal(2L, Pragma("synchronous"));
        Assert.Equal(1L, Pragma("foreign_keys"));
        if (profile != SqliteDatabaseProfile.History)
        {
            Assert.Equal(16384L, Pragma("page_size"));
            Assert.Equal(
                profile == SqliteDatabaseProfile.DiagnosticBoundedFile ? 1L : 2L,
                Pragma("temp_store")
            );
        }
        if (
            profile
            is SqliteDatabaseProfile.DiagnosticFile
                or SqliteDatabaseProfile.DiagnosticBoundedFile
        )
        {
            if (profile == SqliteDatabaseProfile.DiagnosticFile)
                Assert.Equal(0L, Pragma("cache_spill"));
            else
            {
                Assert.Equal(-65536L, Pragma("cache_size"));
                Assert.True((long)Pragma("cache_spill")! > 0);
            }
            Assert.Equal("exclusive", Pragma("locking_mode"));
        }
        else
            Assert.Equal("normal", Pragma("locking_mode"));
        Assert.Equal("ok", Pragma("integrity_check"));
    }

    [Fact]
    public void BufferedEstimate_UsesMetadataLengthsRatherThanFileContentsAndSupportsBoundedPublication()
    {
        var token = TestContext.Current.CancellationToken;
        var small = new BufferedScan(Descriptor());
        var large = new BufferedScan(Descriptor());
        foreach (var fact in BasicFacts(1))
            small.Add(fact, token);
        foreach (var fact in BasicFacts(long.MaxValue))
            large.Add(fact, token);
        Assert.Throws<InvalidOperationException>(() => small.EstimateDatabaseBytes(token));
        var expected = small.Complete(token);
        large.Complete(token);
        Assert.Equal(small.EstimateDatabaseBytes(token), large.EstimateDatabaseBytes(token));
        Assert.True(small.EstimateDatabaseBytes(token) >= 64 * 1024);
        Assert.Equal(0UL, small.EstimateDatabaseBytes(token) % 16384);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            small.EstimateDatabaseBytes(cancelled.Token)
        );
        using var temporary = TemporaryScanDatabase.CreateSqlite(token, boundedMemory: true);
        var id = temporary.Store.PublishBufferedScan(small, Started.AddSeconds(1), token);
        var actual = temporary.Store.ReadAggregation(id, token);
        foreach (var pair in expected.Folders)
            Assert.Equal(pair.Value, actual.Folders[pair.Key]);
        Assert.Equal(
            id,
            temporary.Store.GetPublishedScan(Descriptor().VolumeKey, Descriptor().Range)
        );
        Assert.Throws<ArgumentException>(() =>
            TemporaryScanDatabase.CreateSqlite(token, inMemory: true, boundedMemory: true)
        );
    }

    [Theory]
    [InlineData(64UL * 1024 * 1024)]
    [InlineData(256UL * 1024 * 1024)]
    [InlineData(2UL * 1024 * 1024 * 1024)]
    public void BoundedFileCache_AppliesTheSelectedKiBBudgetAndPreservesPublication(
        ulong cacheBytes
    )
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new DatabaseFixture();
        string path = Path.Combine(
            Path.GetDirectoryName(fixture.DatabasePath)!,
            "cache-profile.sqlite3"
        );
        using (
            var context = SqliteDatabaseBootstrap.Open(
                path,
                token,
                SqliteDatabaseProfile.DiagnosticBoundedFile,
                cacheBytes
            )
        )
        {
            using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA cache_size";
            Assert.Equal(-checked((long)(cacheBytes / 1024)), command.ExecuteScalar());
            command.CommandText = "PRAGMA cache_spill";
            Assert.True((long)command.ExecuteScalar()! > 0);
        }
        using var temporary = TemporaryScanDatabase.CreateSqlite(
            token,
            boundedMemory: true,
            fileCacheBytes: cacheBytes
        );
        var buffer = new BufferedScan(Descriptor());
        foreach (var fact in BasicFacts())
            buffer.Add(fact, token);
        var expected = buffer.Complete(token);
        var id = temporary.Store.PublishBufferedScan(buffer, Started.AddSeconds(1), token);
        var actual = temporary.Store.ReadAggregation(id, token);
        foreach (var pair in expected.Folders)
            Assert.Equal(pair.Value, actual.Folders[pair.Key]);
    }

    [Fact]
    public void BoundedFileCache_RejectsInvalidBudgetsBeforeOpeningAFile()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new DatabaseFixture();
        string path = Path.Combine(
            Path.GetDirectoryName(fixture.DatabasePath)!,
            "invalid-cache.sqlite3"
        );
        foreach (
            ulong bytes in new ulong[]
            {
                0,
                1024 * 1024,
                64 * 1024 * 1024 - 1,
                64 * 1024 * 1024 + 1,
                2UL * 1024 * 1024 * 1024 + 16384,
                ulong.MaxValue,
            }
        )
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SqliteDatabaseBootstrap.Open(
                    path,
                    token,
                    SqliteDatabaseProfile.DiagnosticBoundedFile,
                    bytes
                )
            );
        Assert.Throws<ArgumentException>(() =>
            SqliteDatabaseBootstrap.Open(
                path,
                token,
                SqliteDatabaseProfile.DiagnosticFile,
                fileCacheBytes: 64 * 1024 * 1024
            )
        );
        Assert.Throws<ArgumentException>(() =>
            TemporaryScanDatabase.CreateSqlite(token, fileCacheBytes: 64 * 1024 * 1024)
        );
        Assert.False(System.IO.File.Exists(path));
    }

    [Fact]
    public void ParentFolder_UsesVisibleNameAndPreservesBothSequenceIdentities()
    {
        using var fixture = new DatabaseFixture();
        var token = TestContext.Current.CancellationToken;
        var buffer = new BufferedScan(Descriptor());
        foreach (var fact in BasicFacts())
            buffer.Add(fact, token);
        buffer.Add(Stage(Directory(22, Ref(20))), token);
        buffer.Add(Stage(Directory(23, Ref(20, 99))), token);
        buffer.Add(
            Stage(
                Directory(24, Root) with
                {
                    Names = [new(Root, "SHORT~1", FileNameNamespace.Dos, 0)],
                }
            ),
            token
        );
        buffer.Add(
            Stage(
                Directory(25, Root) with
                {
                    Names =
                    [
                        new(Root, "ambiguous", FileNameNamespace.Win32, 0),
                        new(Ref(20), "ambiguous", FileNameNamespace.Win32, 0),
                    ],
                }
            ),
            token
        );
        buffer.Complete(token);
        var id = fixture.Store.PublishBufferedScan(buffer, Started.AddSeconds(1), token);

        Assert.Equal(Root, fixture.Store.ReadParentFolder(id, Ref(20), "synthetic-0", token));
        Assert.Equal(Ref(20), fixture.Store.ReadParentFolder(id, Ref(22), "synthetic-0", token));
        Assert.Null(fixture.Store.ReadParentFolder(id, Root, "synthetic-0", token));
        Assert.Null(fixture.Store.ReadParentFolder(id, Ref(22), "wrong-name", token));
        Assert.Null(fixture.Store.ReadParentFolder(id, Ref(23), "synthetic-0", token));
        Assert.Null(fixture.Store.ReadParentFolder(id, Ref(24), "SHORT~1", token));
        Assert.Null(fixture.Store.ReadParentFolder(id, Ref(25), "ambiguous", token));
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Store.ReadParentFolder(id, Ref(22, 99), "synthetic-0", token)
        );
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            fixture.Store.ReadParentFolder(id, Ref(22), "synthetic-0", cancellation.Token)
        );
    }

    [Fact]
    public void FolderContents_PreserveAliasesUnknownSizesAndParentSequenceWhilePaging()
    {
        using var fixture = new DatabaseFixture();
        var token = TestContext.Current.CancellationToken;
        var buffer = new BufferedScan(Descriptor());
        foreach (var fact in BasicFacts())
            buffer.Add(fact, token);
        var named = File(110, 10, Root) with
        {
            Names =
            [
                new(Root, "日本語😀", FileNameNamespace.Win32, 0),
                new(Root, "SHORT~1", FileNameNamespace.Dos, 0),
            ],
            Streams = [new("", 10, 4096), new("extra", 10000, 16384)],
        };
        var unknown = File(111, 0, Root) with
        {
            Resolution = CapacityResolution.Unresolved,
            Streams = [],
        };
        var reusedParent = File(112, 500, Ref(5, 2));
        buffer.Add(Stage(named), token);
        buffer.Add(Stage(unknown), token);
        buffer.Add(Stage(reusedParent), token);
        buffer.Complete(token);
        var id = fixture.Store.PublishBufferedScan(buffer, Started.AddSeconds(1), token);

        var first = fixture.Store.ReadFolderEntries(
            id,
            Root,
            pageSize: 2,
            cancellationToken: token,
            includeTotalCount: true
        );
        var second = fixture.Store.ReadFolderEntries(
            id,
            Root,
            page: 1,
            pageSize: 2,
            cancellationToken: token
        );

        Assert.True(first.HasMore);
        Assert.Equal(4L, first.TotalCount);
        Assert.Null(second.TotalCount);
        Assert.All(
            first.Entries,
            e =>
            {
                Assert.True(e.IsDirectory);
                Assert.True(e.CanBrowse);
            }
        );
        Assert.False(second.HasMore);
        Assert.Equal(2, second.Entries.Length);
        var found = second.Entries.Single(e => e.Reference == named.Reference);
        Assert.Equal("日本語😀", found.Name);
        Assert.Equal(new CapacityAmount(10, 4096), found.Data);
        var unresolved = second.Entries.Single(e => e.Reference == unknown.Reference);
        Assert.Null(unresolved.Data);
        Assert.True(unresolved.CapacityIncomplete);
        Assert.DoesNotContain(
            first.Entries.Concat(second.Entries),
            e => e.Reference == reusedParent.Reference
        );
        Assert.Equal(
            2,
            fixture
                .Store.ReadFolderEntries(id, Root, directoriesOnly: true, cancellationToken: token)
                .Entries.Length
        );
        Assert.Equal(
            2L,
            fixture
                .Store.ReadFolderEntries(
                    id,
                    Root,
                    pageSize: 1,
                    directoriesOnly: true,
                    cancellationToken: token,
                    includeTotalCount: true
                )
                .TotalCount
        );
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Store.ReadFolderEntries(id, Ref(5, 2), cancellationToken: token)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fixture.Store.ReadFolderEntries(id, Root, page: -1, cancellationToken: token)
        );
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            fixture.Store.ReadFolderEntries(id, Root, cancellationToken: cancelled.Token)
        );
    }

    [Fact]
    public void FolderContents_SortSizesBeforePagingAndKeepKnownZeroAheadOfUnknown()
    {
        using var fixture = new DatabaseFixture();
        var token = TestContext.Current.CancellationToken;
        var buffer = new BufferedScan(Descriptor());
        buffer.Add(Stage(Directory(5, Root)), token);
        buffer.Add(Stage(File(100, 0, Root) with { Streams = [new("", 9000, 4096)] }), token);
        buffer.Add(Stage(File(101, 0, Root) with { Streams = [new("", 100, 8192)] }), token);
        buffer.Add(Stage(File(102, 0, Root) with { Streams = [new("ads", 100000, 4096)] }), token);
        buffer.Add(
            Stage(
                File(103, 0, Root) with
                {
                    Resolution = CapacityResolution.Unresolved,
                    Streams = [],
                }
            ),
            token
        );
        buffer.Complete(token);
        var id = fixture.Store.PublishBufferedScan(buffer, Started.AddSeconds(1), token);

        var allocated = fixture.Store.ReadFolderEntries(
            id,
            Root,
            pageSize: 1,
            cancellationToken: token
        );
        var logical = fixture.Store.ReadFolderEntries(
            id,
            Root,
            pageSize: 1,
            cancellationToken: token,
            sort: FolderEntrySort.LogicalBytes
        );
        var tail = fixture.Store.ReadFolderEntries(
            id,
            Root,
            page: 1,
            pageSize: 2,
            cancellationToken: token
        );

        Assert.Equal(Ref(101), Assert.Single(allocated.Entries).Reference);
        Assert.Equal(Ref(100), Assert.Single(logical.Entries).Reference);
        Assert.Equal(new CapacityAmount(0, 0), tail.Entries[0].Data);
        Assert.Null(tail.Entries[1].Data);
        var ascending = Enumerable
            .Range(0, 4)
            .Select(page =>
                Assert.Single(
                    fixture
                        .Store.ReadFolderEntries(
                            id,
                            Root,
                            page,
                            pageSize: 1,
                            cancellationToken: token,
                            descending: false
                        )
                        .Entries
                )
            )
            .ToArray();
        Assert.Equal(
            new ulong[] { 102, 100, 101, 103 },
            ascending.Select(row => row.Reference.RecordIndex)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Publication_PreparesParentIndexStatisticsBeforeBrowsingAndReopening(bool buffered)
    {
        using var fixture = new DatabaseFixture();
        var token = TestContext.Current.CancellationToken;
        var descriptor = Descriptor(range: new(0, 10000));
        var facts = new List<StagedObservation> { Stage(Directory(5, Root)) };
        for (ulong i = 20; i < 120; i++)
            facts.Add(Stage(Directory(i, Root)));
        for (ulong i = 1000; i < 4000; i++)
            facts.Add(Stage(File(i, checked((long)i), Ref(20 + i % 100))));

        Guid scan;
        if (buffered)
        {
            var buffer = new BufferedScan(descriptor);
            foreach (var fact in facts)
                buffer.Add(fact, token);
            buffer.Complete(token);
            scan = fixture.Store.PublishBufferedScan(buffer, Started.AddSeconds(1), token);
        }
        else
        {
            scan = fixture.Store.BeginScan(descriptor, token);
            foreach (var batch in facts.Chunk(ScanStore.MaximumBatchSize))
                fixture.Store.StageBatch(scan, batch, token);
            fixture.Store.CompleteScan(scan, Started.AddSeconds(1), token);
        }
        var entries = fixture.Store.ReadFolderEntries(scan, Ref(20), cancellationToken: token);
        Assert.Equal(30, entries.Entries.Length);
        Assert.All(entries.Entries, entry => Assert.NotNull(entry.Data));
        fixture.Store.Dispose();
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = fixture.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()
        );
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT stat FROM sqlite_stat1 WHERE idx = 'IX_file_name_ScanId_ParentIndex_ParentSequence'";
        var statistics = Assert.IsType<string>(command.ExecuteScalar());
        Assert.True(
            long.Parse(statistics.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture)
                > 0
        );
    }

    [Fact]
    public void LargestFolders_SizeBasisChangesRankingWithoutChangingSavedCapacity()
    {
        using var fixture = new DatabaseFixture();
        var token = TestContext.Current.CancellationToken;
        var buffer = new BufferedScan(Descriptor());
        foreach (var fact in BasicFacts().Where(f => f.RecordIndex != 100))
            buffer.Add(fact, token);
        buffer.Add(Stage(File(100, 0, Ref(20)) with { Streams = [new("", 10000, 4096)] }), token);
        buffer.Add(Stage(File(101, 0, Ref(21)) with { Streams = [new("", 1000, 8192)] }), token);
        buffer.Complete(token);
        var id = fixture.Store.PublishBufferedScan(buffer, Started.AddSeconds(1), token);

        var disk = fixture.Store.ReadLargestFolders(id, cancellationToken: token);
        var logical = fixture.Store.ReadLargestFolders(
            id,
            cancellationToken: token,
            sort: FolderEntrySort.LogicalBytes
        );

        Assert.Equal(Ref(21), disk[0].Sample.Reference);
        Assert.Equal(Ref(20), logical[0].Sample.Reference);
        Assert.Equal(new CapacityAmount(10000, 4096), logical[0].Data);
        Assert.Equal(logical[0].Data, disk[1].Data);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fixture.Store.ReadLargestFolders(
                id,
                cancellationToken: token,
                sort: (FolderEntrySort)42
            )
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DerivedReadModelPublication_FailureOrCancellationRollsBackWithSourceFacts(
        bool cancel
    )
    {
        var token = TestContext.Current.CancellationToken;
        using var store = ScanStore.OpenInMemory(token);
        var descriptor = Descriptor();
        var buffer = new BufferedScan(descriptor);
        foreach (var fact in BasicFacts())
            buffer.Add(fact, token);
        buffer.Complete(token);
        var previous = store.PublishBufferedScan(buffer, Started.AddSeconds(1), token);
        store.HistoryContext.Database.ExecuteSqlRaw(
            "CREATE TABLE read_model_probe(Value INTEGER);"
        );
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);

        void Publish() =>
            store.PublishHistoryBuffer(
                buffer,
                Started.AddSeconds(2),
                new()
                {
                    VolumeKey = descriptor.VolumeKey,
                    Drive = "Synthetic",
                    CompletedUtcTicks = Started.AddSeconds(2).UtcTicks,
                    LocalDay = DateOnly.FromDateTime(Started.UtcDateTime).DayNumber,
                },
                cancellation.Token,
                (context, _) =>
                {
                    context.Database.ExecuteSqlRaw("INSERT INTO read_model_probe VALUES(1);");
                    if (cancel)
                        cancellation.Cancel();
                    else
                        throw new InvalidOperationException("Derived read model failed.");
                }
            );

        if (cancel)
            Assert.Throws<OperationCanceledException>(Publish);
        else
            Assert.Throws<InvalidOperationException>(Publish);

        Assert.Equal(previous, store.GetPublishedScan(descriptor.VolumeKey, descriptor.Range));
        Assert.Equal(1, store.HistoryContext.Scans.Count());
        Assert.Empty(store.HistoryContext.HistorySnapshots);
        using var command = store.HistoryContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM read_model_probe;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    private sealed class SyntheticSource(byte[] root, byte[] file) : IMftScanSource
    {
        public MftRecordEncoding Encoding => MftRecordEncoding.RawProtected;

        public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
            ulong recordIndex,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                recordIndex switch
                {
                    5 => root,
                    42 => file,
                    _ => null,
                }
            );
        }

        public async IAsyncEnumerable<MftRecordCandidate> EnumerateAsync(
            MftScanRange range,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(42, file);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(5, root);
        }
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(),
            $"something-storage-test-{Guid.NewGuid():N}"
        );
        public string DatabasePath => Path.Combine(directory, "fixture.sqlite3");
        public ScanStore Store { get; private set; }

        public DatabaseFixture()
        {
            System.IO.Directory.CreateDirectory(directory);
            Store = ScanStore.OpenTrustedPath(DatabasePath, TestContext.Current.CancellationToken);
        }

        public void Reopen()
        {
            Store.Dispose();
            Store = ScanStore.OpenTrustedPath(DatabasePath, TestContext.Current.CancellationToken);
        }

        public void Dispose()
        {
            Store.Dispose();
            string tempRoot =
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
                + Path.DirectorySeparatorChar;
            string owned = Path.GetFullPath(directory);
            if (
                !owned.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(owned)
                    .StartsWith("something-storage-test-", StringComparison.Ordinal)
            )
                throw new InvalidOperationException("Invalid private fixture cleanup path.");
            System.IO.Directory.Delete(owned, recursive: true);
        }
    }
}
