using Something.Ntfs;

namespace Something.Ntfs.Tests;

public sealed class CompressedExtentAssemblyTests
{
    private static readonly FileReference Base = FileReference.Create(42, 3);
    private static readonly FileReference Extension = FileReference.Create(43, 3);

    [Theory]
    [InlineData(NtfsAttributeFlags.Compressed, false)]
    [InlineData(NtfsAttributeFlags.Sparse, false)]
    [InlineData(NtfsAttributeFlags.Compressed | NtfsAttributeFlags.Sparse, false)]
    [InlineData(NtfsAttributeFlags.Compressed, true)]
    [InlineData(NtfsAttributeFlags.Sparse, true)]
    [InlineData(NtfsAttributeFlags.Compressed | NtfsAttributeFlags.Sparse, true)]
    public async Task ReadAsync_ContinuationOmitsCompressionUnit_AssemblesAllPhysicalRunsWithoutChangingAttributes(
        NtfsAttributeFlags flags,
        bool named
    )
    {
        var (baseBytes, extensionBytes) = Fixture(flags, name: named ? "synthetic-stream" : "");
        byte[] baseCopy = baseBytes.ToArray();
        byte[] extensionCopy = extensionBytes.ToArray();
        var source = new Source(baseBytes, extensionBytes);

        var result = await new LiveMftFileReader(4096).ReadAsync(
            source,
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        var stream = Assert.Single(result.File!.DataStreams);
        Assert.Equal(100000, stream.LogicalBytes);
        Assert.Equal(65536, stream.AllocatedBytes);
        Assert.Equal(4, stream.Runs.Length);
        Assert.Equal(named ? "synthetic-stream" : "", stream.Name);
        Assert.Equal(new NtfsStreamStorage(flags, 4), stream.Storage);
        var extents = result.File.Attributes.OfType<NonResidentAttribute>().ToArray();
        Assert.Equal(4, extents[0].CompressionUnit);
        Assert.Equal(0, extents[1].CompressionUnit);
        Assert.Equal(baseCopy, baseBytes);
        Assert.Equal(extensionCopy, extensionBytes);
    }

    [Fact]
    public async Task ReadAsync_ContinuationExplicitlyRepeatsCompressionUnit_StillAssembles()
    {
        var (baseBytes, extensionBytes) = Fixture(NtfsAttributeFlags.Compressed, nextUnit: 4);
        var result = await new LiveMftFileReader(4096).ReadAsync(
            new Source(baseBytes, extensionBytes),
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        Assert.Equal(65536, Assert.Single(result.File!.DataStreams).AllocatedBytes);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task ReadAsync_OmittedUnit_DoesNotHideOtherConflictsOrMissingExtents(int scenario)
    {
        var (baseBytes, extensionBytes) = Fixture(
            NtfsAttributeFlags.Compressed,
            physical: scenario == 6 ? 65537 : 65536,
            logical: scenario == 7 ? 200000 : 100000
        );
        var source = new Source(baseBytes, scenario == 8 ? null : extensionBytes);

        var result = await new LiveMftFileReader(4096).ReadAsync(
            source,
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(
            scenario == 8 ? LiveFileReadStatus.Missing : LiveFileReadStatus.InvalidAssembly,
            result.Status
        );
        Assert.Null(result.File);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 255)]
    [InlineData(4, 3)]
    [InlineData(8, 5)]
    [InlineData(16, 0)]
    public async Task ReadAsync_ContinuationUnitIsNotAStreamSetting(byte firstUnit, byte nextUnit)
    {
        var (baseBytes, extensionBytes) = Fixture(
            NtfsAttributeFlags.Compressed,
            firstUnit: firstUnit,
            nextUnit: nextUnit
        );

        var result = await new LiveMftFileReader(4096).ReadAsync(
            new Source(baseBytes, extensionBytes),
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        var file = Assert.IsType<NtfsFile>(result.File);
        var stream = Assert.Single(file.DataStreams);
        Assert.Equal(firstUnit, stream.Storage!.CompressionUnit);
        Assert.Equal(65536, stream.AllocatedBytes);
        Assert.Equal(
            nextUnit,
            file.Attributes.OfType<NonResidentAttribute>().Last().CompressionUnit
        );
    }

    [Fact]
    public async Task ReadAsync_StableFlagsDifference_ReturnsUnsupportedWithoutCapacity()
    {
        var (baseBytes, extensionBytes) = Fixture(NtfsAttributeFlags.Compressed);
        // Raw flags are different, but this alone does not prove an invalid NTFS file.
        RecordFixture.U16(extensionBytes, 56 + 12, 0);

        var result = await new LiveMftFileReader(4096).ReadAsync(
            new Source(baseBytes, extensionBytes),
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(LiveFileReadStatus.Unsupported, result.Status);
        Assert.Contains("flags mismatch", result.Error);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task ReadAsync_FlagsChangeDuringRevalidation_RetriesBeforeReportingUnsupported()
    {
        var (baseBytes, validExtension) = Fixture(NtfsAttributeFlags.Compressed);
        byte[] earlierExtension = validExtension.ToArray();
        RecordFixture.U16(earlierExtension, 56 + 12, 0);
        var source = new ChangingSource(baseBytes, earlierExtension, validExtension);

        var result = await new LiveMftFileReader(4096).ReadAsync(
            source,
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(65536, Assert.Single(result.File!.DataStreams).AllocatedBytes);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    public async Task ReadAsync_OmittedUnit_DoesNotHideOverlappingOrMissingVcnRanges(long nextVcn)
    {
        var (baseBytes, extensionBytes) = Fixture(NtfsAttributeFlags.Compressed, nextVcn: nextVcn);
        var result = await new LiveMftFileReader(4096).ReadAsync(
            new Source(baseBytes, extensionBytes),
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Equal(LiveFileReadStatus.InvalidAssembly, result.Status);
        Assert.Contains("VCN discontinuity", result.Error);
        Assert.Null(result.File);
    }

    private static (byte[] Base, byte[] Extension) Fixture(
        NtfsAttributeFlags flags,
        string name = "",
        ushort firstUnit = 4,
        ushort nextUnit = 0,
        long physical = 65536,
        long logical = 100000,
        long nextVcn = 16
    )
    {
        byte[] first = RecordFixture.NonResident(
            id: 1,
            name: name,
            highestVcn: 15,
            flags: flags,
            runs: [0x11, 8, 100, 0x01, 8, 0],
            logical: logical,
            allocated: 131072,
            physical: physical
        );
        byte[] next = RecordFixture.NonResident(
            id: 7,
            name: name,
            lowestVcn: nextVcn,
            highestVcn: nextVcn + 15,
            flags: flags,
            runs: [0x11, 8, 110, 0x01, 8, 0]
        );
        RecordFixture.U16(first, 34, firstUnit);
        RecordFixture.U16(next, 34, nextUnit);
        byte[] list =
        [
            .. RecordFixture.ListEntry(0x80, 1, Base, name: name),
            .. RecordFixture.ListEntry(0x80, 0, Extension, nextVcn, name),
        ];
        return (
            RecordFixture.Record([RecordFixture.Resident(0x20, 0, list), first]),
            RecordFixture.Record([next], baseReference: Base.Value, index: 43)
        );
    }

    private sealed class Source(byte[] baseBytes, byte[]? extensionBytes) : IRawMftRecordSource
    {
        public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
            ulong recordIndex,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? bytes = recordIndex == 42 ? baseBytes : extensionBytes;
            return ValueTask.FromResult(
                bytes is null ? (ReadOnlyMemory<byte>?)null : bytes.AsMemory()
            );
        }
    }

    private sealed class ChangingSource(byte[] baseBytes, byte[] earlier, byte[] current)
        : IRawMftRecordSource
    {
        private int extensionReads;

        public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
            ulong recordIndex,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes =
                recordIndex == 42 ? baseBytes
                : ++extensionReads <= 2 ? earlier
                : current;
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(bytes);
        }
    }
}
