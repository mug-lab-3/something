using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Something.Ntfs;
using Something.Statistics;
using Something.Storage;

namespace Something.Ntfs.Tests;

public sealed class ScanStoreTests
{
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyntheticLiveScan_AdapterStageAndAggregation_WorkWithoutVolumeAccess(
        bool dosAlias
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
                    RecordFixture.FileName("synthetic.bin", parent: Root.Value)
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
        Assert.Equal<ushort?>(dosAlias ? (ushort)2 : (ushort)1, stored.ObservedHardLinkCount);
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
            Assert.Equal(pair.Value, actual.Folders[pair.Key]);
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

    [Fact]
    public void InitialMigration_MatchesModelAndSurvivesReopen()
    {
        using var fixture = new DatabaseFixture();
        using var context = new ScanDbContextFactory().CreateDbContext([]);
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Single(context.Database.GetMigrations());
        fixture.Reopen();
        var id = fixture.Store.BeginScan(Descriptor(), TestContext.Current.CancellationToken);
        Assert.Equal(StoredScanState.Running, fixture.Store.GetScan(id).State);
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
        Assert.Equal(1UL, top[0].Sample.SubtreeUnresolvedCount);
        Assert.Equal(
            new CapacityAmount(10, 10),
            top.Single(t => t.Sample.Reference == Ref(21)).Sample.SubtreeData
        );
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            fixture.Store.ReadLargestFolders(id, cancellationToken: cancel.Token)
        );
    }

    [Fact]
    public void TemporaryScan_DoesNotTouchHistoryAndCanPublishRankAndDispose()
    {
        using var history = new DatabaseFixture();
        var saved = history.Store.BeginScan(Descriptor(), TestContext.Current.CancellationToken);
        history.Store.StageBatch(saved, BasicFacts(), TestContext.Current.CancellationToken);
        history.Store.CompleteScan(
            saved,
            Started.AddSeconds(1),
            TestContext.Current.CancellationToken
        );
        using var temporary = TemporaryScanDatabase.Create(TestContext.Current.CancellationToken);
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
