using System.Collections.Immutable;
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

    [Theory]
    [InlineData(false, 0, NtfsAttributeFlags.Compressed)]
    [InlineData(true, 0, NtfsAttributeFlags.Compressed)]
    [InlineData(false, 4, NtfsAttributeFlags.Compressed)]
    [InlineData(true, 4, NtfsAttributeFlags.Compressed)]
    [InlineData(false, 0, NtfsAttributeFlags.Compressed | NtfsAttributeFlags.Sparse)]
    [InlineData(true, 4, NtfsAttributeFlags.Compressed | NtfsAttributeFlags.Sparse)]
    public async Task ReadAsync_PrimaryCompressedContinuationOmitsFlag_UsesPrimarySettings(
        bool named,
        ushort nextUnit,
        NtfsAttributeFlags primaryFlags
    )
    {
        var (baseBytes, extensionBytes) = Fixture(
            primaryFlags,
            name: named ? "synthetic-stream" : "",
            nextUnit: nextUnit
        );
        RecordFixture.U16(extensionBytes, 56 + 12, 0);
        byte[] baseCopy = baseBytes.ToArray();
        byte[] extensionCopy = extensionBytes.ToArray();

        var result = await new LiveMftFileReader(4096).ReadAsync(
            new Source(baseBytes, extensionBytes),
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        var stream = Assert.Single(result.File!.DataStreams);
        Assert.Equal(100000, stream.LogicalBytes);
        Assert.Equal(65536, stream.AllocatedBytes);
        Assert.Equal(new NtfsStreamStorage(primaryFlags, 4), stream.Storage);
        var attributes = result.File.Attributes.OfType<NonResidentAttribute>().ToArray();
        Assert.Equal((NtfsAttributeFlags)0, attributes[1].Flags);
        Assert.Equal(nextUnit, attributes[1].CompressionUnit);
        Assert.Equal(baseCopy, baseBytes);
        Assert.Equal(extensionCopy, extensionBytes);
    }

    [Theory]
    [InlineData(0, NtfsAttributeFlags.Compressed)]
    [InlineData((NtfsAttributeFlags)2, 0)]
    [InlineData(NtfsAttributeFlags.Compressed, (NtfsAttributeFlags)2)]
    [InlineData(NtfsAttributeFlags.Compressed, NtfsAttributeFlags.Encrypted)]
    [InlineData(NtfsAttributeFlags.Compressed, (NtfsAttributeFlags)0x1000)]
    public void Assemble_UnsupportedCompressionDirectionFormatOrOtherBits_DoesNotReturnCapacity(
        NtfsAttributeFlags first,
        NtfsAttributeFlags next
    )
    {
        // Use a primary header with a complete extended size field, then vary raw observations.
        var (baseBytes, extensionBytes) = Fixture(NtfsAttributeFlags.Sparse);
        var parsed = new MftRecordParser().Parse(baseBytes, 42);
        var primary = Assert.Single(parsed.Attributes.OfType<NonResidentAttribute>());
        parsed = parsed with
        {
            Attributes = parsed
                .Attributes.Select(a => a == primary ? primary with { Flags = first } : a)
                .ToImmutableArray(),
        };
        var extension = new MftRecordParser().Parse(extensionBytes, 43);
        var continuation = Assert.Single(extension.Attributes.OfType<NonResidentAttribute>());
        extension = extension with { Attributes = [continuation with { Flags = next }] };

        var error = Assert.Throws<NtfsAssemblyException>(() =>
            new MftFileAssembler(4096).Assemble(parsed, [extension])
        );

        Assert.Equal(NtfsAssemblyFailureKind.Unsupported, error.Kind);
    }

    [Theory]
    [InlineData(false, false, 4096, 1024)]
    [InlineData(false, true, 4096, 1024)]
    [InlineData(true, false, 4096, 1024)]
    [InlineData(true, true, 4096, 1024)]
    [InlineData(false, false, 512, 512)]
    [InlineData(true, true, 512, 512)]
    [InlineData(false, true, 65536, 4096)]
    [InlineData(true, false, 2097152, 65536)]
    public async Task ReadAsync_SparseBitDiffersBetweenExtents_PreservesPrimarySettingsAndRawFlags(
        bool firstSparse,
        bool named,
        int bytesPerCluster,
        int bytesPerRecord
    )
    {
        NtfsAttributeFlags firstFlags = firstSparse ? NtfsAttributeFlags.Sparse : 0;
        NtfsAttributeFlags nextFlags = firstSparse ? 0 : NtfsAttributeFlags.Sparse;
        long logical = Math.Min(5000, bytesPerCluster * 2 - 1);
        string name = named ? "synthetic-stream" : "";
        byte[] list =
        [
            .. RecordFixture.ListEntry(0x80, 1, Base, name: name),
            .. RecordFixture.ListEntry(0x80, 7, Extension, 2, name),
        ];
        byte[] first = RecordFixture.NonResident(
            name: name,
            flags: firstFlags,
            logical: logical,
            allocated: 4 * bytesPerCluster,
            physical: firstSparse ? 3 * bytesPerCluster : null,
            runs: firstSparse ? [0x11, 1, 100, 0x01, 1, 0] : [0x11, 2, 100, 0]
        );
        byte[] next = RecordFixture.NonResident(
            id: 7,
            name: name,
            lowestVcn: 2,
            highestVcn: 3,
            flags: nextFlags,
            runs: [0x11, 2, 110, 0]
        );
        byte[] baseBytes = RecordFixture.Record(
            [RecordFixture.Resident(0x20, 0, list), first],
            size: bytesPerRecord
        );
        byte[] extensionBytes = RecordFixture.Record(
            [next],
            size: bytesPerRecord,
            baseReference: Base.Value,
            index: 43
        );
        byte[] baseCopy = baseBytes.ToArray();
        byte[] extensionCopy = extensionBytes.ToArray();

        var result = await new LiveMftFileReader(bytesPerCluster, totalClusters: 1024).ReadAsync(
            new Source(baseBytes, extensionBytes),
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        var stream = Assert.Single(result.File!.DataStreams);
        Assert.Equal((firstSparse ? 3 : 4) * bytesPerCluster, stream.AllocatedBytes);
        Assert.Equal(logical, stream.LogicalBytes);
        Assert.Equal(firstFlags, stream.Storage!.Flags);
        Assert.Equal(name, stream.Name);
        var extents = result.File.Attributes.OfType<NonResidentAttribute>().ToArray();
        Assert.Equal(firstFlags, extents[0].Flags);
        Assert.Equal(nextFlags, extents[1].Flags);
        Assert.Equal(baseCopy, baseBytes);
        Assert.Equal(extensionCopy, extensionBytes);
    }

    [Theory]
    [InlineData(NtfsAttributeFlags.Compressed)]
    [InlineData(NtfsAttributeFlags.Encrypted)]
    [InlineData((NtfsAttributeFlags)2)]
    [InlineData((NtfsAttributeFlags)0x1000)]
    public async Task ReadAsync_SparseDifferenceWithOtherFlagChanges_RemainsUnsupported(
        NtfsAttributeFlags additionalDifference
    )
    {
        var (baseBytes, extensionBytes) = Fixture(NtfsAttributeFlags.Sparse);
        RecordFixture.U16(extensionBytes, 56 + 12, (ushort)additionalDifference);

        var result = await new LiveMftFileReader(4096).ReadAsync(
            new Source(baseBytes, extensionBytes),
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(LiveFileReadStatus.Unsupported, result.Status);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task ReadAsync_SparseBitChangesDuringRevalidation_StillRetries()
    {
        var (baseBytes, current) = Fixture(NtfsAttributeFlags.Compressed);
        byte[] earlier = current.ToArray();
        RecordFixture.U16(
            earlier,
            56 + 12,
            (ushort)(NtfsAttributeFlags.Compressed | NtfsAttributeFlags.Sparse)
        );

        var result = await new LiveMftFileReader(4096).ReadAsync(
            new ChangingSource(baseBytes, earlier, current),
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        Assert.Equal(2, result.Attempts);
    }

    [Theory]
    [InlineData(0, LiveFileReadStatus.InvalidAssembly, NtfsAttributeFlags.Sparse)]
    [InlineData(1, LiveFileReadStatus.InvalidAssembly, NtfsAttributeFlags.Sparse)]
    [InlineData(2, LiveFileReadStatus.Missing, NtfsAttributeFlags.Sparse)]
    [InlineData(3, LiveFileReadStatus.InvalidAssembly, NtfsAttributeFlags.Sparse)]
    [InlineData(4, LiveFileReadStatus.InvalidAssembly, NtfsAttributeFlags.Sparse)]
    [InlineData(0, LiveFileReadStatus.InvalidAssembly, NtfsAttributeFlags.Compressed)]
    [InlineData(1, LiveFileReadStatus.InvalidAssembly, NtfsAttributeFlags.Compressed)]
    [InlineData(2, LiveFileReadStatus.Missing, NtfsAttributeFlags.Compressed)]
    [InlineData(3, LiveFileReadStatus.InvalidAssembly, NtfsAttributeFlags.Compressed)]
    [InlineData(4, LiveFileReadStatus.InvalidAssembly, NtfsAttributeFlags.Compressed)]
    public async Task ReadAsync_OmittedExtentSetting_DoesNotBypassCapacityOrMappingValidation(
        int scenario,
        LiveFileReadStatus expected,
        NtfsAttributeFlags primaryFlags
    )
    {
        var (baseBytes, next) = Fixture(
            primaryFlags,
            physical: scenario == 0 ? 65537 : 65536,
            logical: scenario == 1 ? 200000 : 100000,
            nextVcn: scenario == 4 ? 17 : 16
        );
        RecordFixture.U16(next, 56 + 12, 0);

        var result = await new LiveMftFileReader(
            4096,
            totalClusters: scenario == 3 ? 111 : 1024
        ).ReadAsync(
            new Source(baseBytes, scenario == 2 ? null : next),
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(expected, result.Status);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task ReadAsync_CompressionBitChangesDuringRevalidation_RetriesBeforeAccepting()
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
