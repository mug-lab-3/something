using System.Runtime.CompilerServices;
using Something.Ntfs;

namespace Something.Ntfs.Tests;

public sealed class LiveMftScannerTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(256, true)]
    [InlineData(1024, true)]
    [InlineData(2048, true)]
    public async Task ScanAsync_SelectedRecordBudget_ControlsCompleteAssembly(
        int limit,
        bool resolves
    )
    {
        var reference = FileReference.Create(42, 3);
        byte[] list =
        [
            .. RecordFixture.ListEntry(0x80, 1, reference),
            .. RecordFixture.ListEntry(0x80, 1, FileReference.Create(43, 3), 2),
        ];
        byte[] baseBytes = RecordFixture.Record([
            RecordFixture.Resident(0x20, 0, list),
            RecordFixture.NonResident(allocated: 16384),
        ]);
        byte[] extension = RecordFixture.Record(
            [RecordFixture.NonResident(lowestVcn: 2, highestVcn: 3)],
            baseReference: reference.Value,
            index: 43
        );
        var source = new Source([42])
        {
            Candidate = _ => baseBytes,
            Read = (index, _) => index == 42 ? baseBytes : extension,
        };
        var entries = new List<MftScanEntry>();

        await foreach (
            var entry in new LiveMftScanner(4096, 1024, limit).ScanAsync(
                source,
                new(42, 1),
                TestContext.Current.CancellationToken
            )
        )
            entries.Add(entry);

        var result = Assert.Single(entries);
        Assert.Equal(
            resolves ? MftScanEntryKind.ResolvedFile : MftScanEntryKind.Unresolved,
            result.Kind
        );
        if (resolves)
        {
            Assert.Equal(16384, Assert.Single(result.File!.DataStreams).AllocatedBytes);
            Assert.Equal(8, source.ReadCalls);
        }
        else
        {
            Assert.Equal(LiveFileReadStatus.LimitExceeded, result.Status);
            Assert.Null(result.File);
            Assert.Equal(2, source.ReadCalls);
        }
        Assert.Equal(1, source.EnumerationDisposals);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RecordBudgetOutsideBounds_Rejects(int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LiveMftScanner(4096, maximumRecords: limit)
        );

    [Fact]
    public async Task ScanAsync_OutOfVolumeRun_YieldsUnresolvedWithoutCapacity()
    {
        var source = new Source([42]);
        var entries = new List<MftScanEntry>();

        await foreach (
            var entry in new LiveMftScanner(4096, 101).ScanAsync(
                source,
                new(42, 1),
                TestContext.Current.CancellationToken
            )
        )
            entries.Add(entry);

        var unresolved = Assert.Single(entries);
        Assert.Equal(MftScanEntryKind.Unresolved, unresolved.Kind);
        Assert.Equal(LiveFileReadStatus.InvalidAssembly, unresolved.Status);
        Assert.Null(unresolved.File);
        Assert.Contains("volume bounds", unresolved.Error);
        Assert.Equal(1, source.EnumerationDisposals);
    }

    private static byte[] Record(ulong index, MftRecordFlags flags = MftRecordFlags.InUse) =>
        RecordFixture.Record(index: (uint)index, flags: flags);

    [Fact]
    public async Task ScanAsync_StableFilesDirectoriesAndExtensions_YieldsCapacityOnlyForBaseFiles()
    {
        var source = new Source([44, 43, 42])
        {
            Candidate = index =>
                index == 43
                    ? RecordFixture.Record(
                        baseReference: FileReference.Create(42, 3).Value,
                        index: 43
                    )
                    : Record(
                        index,
                        index == 44
                            ? MftRecordFlags.InUse | MftRecordFlags.Directory
                            : MftRecordFlags.InUse
                    ),
            Read = (index, _) =>
                index == 43
                    ? RecordFixture.Record(
                        baseReference: FileReference.Create(42, 3).Value,
                        index: 43
                    )
                    : Record(
                        index,
                        index == 44
                            ? MftRecordFlags.InUse | MftRecordFlags.Directory
                            : MftRecordFlags.InUse
                    ),
        };
        var entries = await Collect(source);
        Assert.Equal([44uL, 43uL, 42uL], entries.Select(e => e.RecordIndex));
        Assert.True(entries[0].File!.IsDirectory);
        Assert.Equal(MftScanEntryKind.ExtensionRecord, entries[1].Kind);
        Assert.Null(entries[1].File);
        Assert.Equal(5000, Assert.Single(entries[2].File!.DataStreams).LogicalBytes);
        Assert.Equal(1, source.EnumerationDisposals);
    }

    [Theory]
    [InlineData(LiveFileReadStatus.Missing)]
    [InlineData(LiveFileReadStatus.NotInUse)]
    [InlineData(LiveFileReadStatus.Replaced)]
    [InlineData(LiveFileReadStatus.ChangedDuringRead)]
    [InlineData(LiveFileReadStatus.InvalidRecord)]
    public async Task ScanAsync_UnresolvedAcquisition_NeverYieldsZeroCapacity(
        LiveFileReadStatus status
    )
    {
        // Make the candidate an extension so the record-only revalidation path is also exercised.
        var source = new Source([42])
        {
            Candidate = _ => RecordFixture.Record(baseReference: FileReference.Create(40, 3).Value),
            Read = (index, n) =>
            {
                if (status == LiveFileReadStatus.Missing)
                    return null;
                if (status == LiveFileReadStatus.InvalidRecord)
                    return new byte[1024];
                byte[] bytes = Record(
                    index,
                    status == LiveFileReadStatus.NotInUse ? 0 : MftRecordFlags.InUse
                );
                if (status == LiveFileReadStatus.Replaced)
                    RecordFixture.U16(bytes, 16, 4);
                if (status == LiveFileReadStatus.ChangedDuringRead)
                    RecordFixture.U64(bytes, 8, (ulong)n);
                return bytes;
            },
        };
        var entry = Assert.Single(await Collect(source));
        Assert.Equal(MftScanEntryKind.Unresolved, entry.Kind);
        Assert.Equal(status, entry.Status);
        Assert.Null(entry.File);
    }

    [Fact]
    public async Task ScanAsync_BaseIdentityReusedAfterEnumeration_DoesNotSwitchFiles()
    {
        var source = new Source([42])
        {
            Read = (index, _) =>
            {
                byte[] bytes = Record(index);
                RecordFixture.U16(bytes, 16, 4);
                return bytes;
            },
        };
        var entry = Assert.Single(await Collect(source));
        Assert.Equal(LiveFileReadStatus.Replaced, entry.Status);
        Assert.Null(entry.File);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanAsync_InvalidOrOversizedCandidate_CanRecoverFromBoundedFreshReads(
        bool oversized
    )
    {
        var source = new Source([42])
        {
            Candidate = _ => new byte[oversized ? MftRecordParser.MaximumRecordSize + 1 : 1024],
        };
        var entry = Assert.Single(await Collect(source));
        Assert.Equal(MftScanEntryKind.ResolvedFile, entry.Kind);
        Assert.NotNull(entry.File);
    }

    [Fact]
    public async Task ScanAsync_EnumerationCandidateNotInUse_RevalidatesCurrentState()
    {
        var source = new Source([42]) { Candidate = index => Record(index, 0) };
        var entry = Assert.Single(await Collect(source));
        Assert.Equal(MftScanEntryKind.ResolvedFile, entry.Kind);
    }

    [Theory]
    [InlineData(42, 42)]
    [InlineData(42, 43)]
    [InlineData(45, 42)]
    [InlineData(41, 40)]
    public async Task ScanAsync_InvalidEnumerationOrderOrRange_Throws(int first, int second)
    {
        var source = new Source([(ulong)first, (ulong)second]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Collect(source));
        Assert.Equal(1, source.EnumerationDisposals);
    }

    [Fact]
    public async Task ScanAsync_FileAssemblyFailure_RemainsUnresolved()
    {
        byte[] bytes = RecordFixture.Record([RecordFixture.NonResident(logical: 9000)]);
        var source = new Source([42]) { Candidate = _ => bytes, Read = (_, _) => bytes };
        var entry = Assert.Single(await Collect(source));
        Assert.Equal(LiveFileReadStatus.InvalidAssembly, entry.Status);
        Assert.Null(entry.File);
    }

    [Fact]
    public async Task ScanAsync_EmptyCandidates_IsNotAnErrorOrEvidenceOfDeletion()
    {
        var source = new Source([]);
        Assert.Empty(await Collect(source));
        Assert.Equal(0, source.ReadCalls);
    }

    [Fact]
    public async Task ScanAsync_StopAfterFirstEntry_DisposesEnumerationWithoutReadingNext()
    {
        var source = new Source([44, 42]);
        await foreach (
            var entry in new LiveMftScanner(4096).ScanAsync(
                source,
                new(42, 3),
                TestContext.Current.CancellationToken
            )
        )
        {
            Assert.Equal(44uL, entry.RecordIndex);
            break;
        }
        Assert.Equal(1, source.Candidates);
        Assert.Equal(1, source.EnumerationDisposals);
    }

    [Fact]
    public async Task ScanAsync_OperationalFailure_PropagatesAndDisposesEnumeration()
    {
        var error = new IOException("synthetic failure");
        var source = new Source([42]) { Read = (_, _) => throw error };
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => Collect(source)));
        Assert.Equal(1, source.EnumerationDisposals);
    }

    [Fact]
    public async Task ScanAsync_CancellationBetweenEntries_StopsWithoutAcquiringNext()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var source = new Source([44, 42]);
        await using var iterator = new LiveMftScanner(4096)
            .ScanAsync(source, new(42, 3), cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        Assert.True(await iterator.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await iterator.MoveNextAsync()
        );
        Assert.Equal(1, source.Candidates);
    }

    [Fact]
    public async Task ScanAsync_EmptyDefaultRange_IsRejected()
    {
        var source = new Source([]);
        await Assert.ThrowsAsync<ArgumentException>(() => Collect(source, default(MftScanRange)));
        Assert.Equal(0, source.Candidates);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(281474976710656, 1)]
    [InlineData(281474976710655, 2)]
    [InlineData(0, ulong.MaxValue)]
    public void Range_InvalidBounds_Rejects(ulong start, ulong count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MftScanRange(start, count));

    [Fact]
    public void Range_LastRecordBoundary_DoesNotOverflow()
    {
        var range = new MftScanRange(FileReference.MaximumRecordIndex, 1);
        Assert.Equal(FileReference.MaximumRecordIndex + 1, range.EndExclusive);
    }

    private static async Task<List<MftScanEntry>> Collect(Source source, MftScanRange? range = null)
    {
        List<MftScanEntry> entries = [];
        await foreach (
            var entry in new LiveMftScanner(4096).ScanAsync(
                source,
                range ?? new(42, 3),
                TestContext.Current.CancellationToken
            )
        )
            entries.Add(entry);
        return entries;
    }

    private sealed class Source(ulong[] indices) : IMftScanSource
    {
        public MftRecordEncoding Encoding => MftRecordEncoding.RawProtected;
        public Func<ulong, byte[]> Candidate { get; init; } = index => Record(index);
        public Func<ulong, int, byte[]?> Read { get; init; } = (index, _) => Record(index);
        public int Candidates { get; private set; }
        public int ReadCalls { get; private set; }
        public int EnumerationDisposals { get; private set; }

        public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
            ulong recordIndex,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? bytes = Read(recordIndex, ++ReadCalls);
            return ValueTask.FromResult(
                bytes is null ? (ReadOnlyMemory<byte>?)null : bytes.AsMemory()
            );
        }

        public async IAsyncEnumerable<MftRecordCandidate> EnumerateAsync(
            MftScanRange range,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await Task.CompletedTask;
            try
            {
                foreach (ulong index in indices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Candidates++;
                    yield return new(index, Candidate(index));
                }
            }
            finally
            {
                EnumerationDisposals++;
            }
        }
    }
}
