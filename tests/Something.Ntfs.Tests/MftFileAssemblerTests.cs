namespace Something.Ntfs.Tests;

public sealed class MftFileAssemblerTests
{
    private static readonly FileReference BaseReference = FileReference.Create(42, 3);
    private static readonly FileReference ExtensionReference = FileReference.Create(43, 3);
    private readonly MftRecordParser parser = new();
    private readonly MftFileAssembler assembler = new(4096);

    private (MftRecord Base, MftRecord Extension) ExtendedFile(bool nonresidentList = false)
    {
        byte[] list =
        [
            .. RecordFixture.ListEntry(0x80, 1, BaseReference),
            .. RecordFixture.ListEntry(0x80, 0, ExtensionReference, 2),
        ];
        byte[] listAttribute = nonresidentList
            ? RecordFixture.NonResident(id: 0, type: 0x20, logical: list.Length)
            : RecordFixture.Resident(0x20, 0, list);
        var baseRecord = parser.Parse(
            RecordFixture.Record([listAttribute, RecordFixture.NonResident(allocated: 16384)]),
            42
        );
        var extension = parser.Parse(
            RecordFixture.Record(
                [RecordFixture.NonResident(id: 7, lowestVcn: 2, highestVcn: 3)],
                baseReference: BaseReference.Value,
                index: 43
            ),
            43
        );
        return (baseRecord, extension);
    }

    [Fact]
    public void Assemble_ExtendedDataStream_CombinesRunsWithoutDoubleCountingSizes()
    {
        var (baseRecord, extension) = ExtendedFile();
        var file = assembler.Assemble(baseRecord, [extension]);
        var stream = Assert.Single(file.DataStreams);
        Assert.Equal(5000, stream.LogicalBytes);
        Assert.Equal(16384, stream.AllocatedBytes);
        Assert.Equal(2, stream.Runs.Length);
        Assert.Equal(2, stream.Runs[1].StartVcn);
    }

    [Fact]
    public void Assemble_MissingExtension_DoesNotReturnPartialCapacity()
    {
        var (baseRecord, _) = ExtendedFile();
        Assert.Throws<NtfsAssemblyException>(() => assembler.Assemble(baseRecord, []));
    }

    [Fact]
    public void Assemble_ReusedExtensionIndex_RejectsStaleSequence()
    {
        var (baseRecord, extension) = ExtendedFile();
        extension = extension with { Reference = FileReference.Create(43, 4) };
        Assert.Throws<NtfsAssemblyException>(() => assembler.Assemble(baseRecord, [extension]));
    }

    [Fact]
    public void Assemble_ExtensionPointsToReusedBase_Rejects()
    {
        var (baseRecord, extension) = ExtendedFile();
        extension = extension with { BaseReference = FileReference.Create(42, 4) };
        Assert.Throws<NtfsAssemblyException>(() => assembler.Assemble(baseRecord, [extension]));
    }

    [Fact]
    public void Assemble_DuplicateExtension_Rejects()
    {
        var (baseRecord, extension) = ExtendedFile();
        Assert.Throws<NtfsAssemblyException>(() =>
            assembler.Assemble(baseRecord, [extension, extension])
        );
    }

    [Fact]
    public void Assemble_NonresidentList_RequiresCompleteSuppliedValue()
    {
        var (baseRecord, extension) = ExtendedFile(nonresidentList: true);
        Assert.Throws<NtfsAssemblyException>(() => assembler.Assemble(baseRecord, [extension]));
        byte[] list =
        [
            .. RecordFixture.ListEntry(0x80, 1, BaseReference),
            .. RecordFixture.ListEntry(0x80, 0, ExtensionReference, 2),
        ];
        Assert.Single(assembler.Assemble(baseRecord, [extension], list).DataStreams);
        Assert.Throws<NtfsAssemblyException>(() =>
            assembler.Assemble(baseRecord, [extension], list.AsMemory(0, list.Length - 1))
        );
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    public void Assemble_OverlappingOrGappedExtents_Rejects(long lowest, long highest)
    {
        var record = parser.Parse(
            RecordFixture.Record([
                RecordFixture.NonResident(),
                RecordFixture.NonResident(id: 2, lowestVcn: lowest, highestVcn: highest),
            ]),
            42
        );
        var error = Assert.Throws<NtfsAssemblyException>(() => assembler.Assemble(record, []));
        Assert.Contains("VCN discontinuity", error.Message);
        Assert.Contains("expected-lowest=2", error.Message);
        Assert.Contains($"observed-lowest={lowest}", error.Message);
    }

    [Fact]
    public void Assemble_InconsistentFlags_ReportsUnsupportedRatherThanProvenCorruption()
    {
        var (baseRecord, extension) = ExtendedFile();
        var attribute = Assert.IsType<NonResidentAttribute>(Assert.Single(extension.Attributes));
        extension = extension with
        {
            Attributes = [attribute with { Flags = NtfsAttributeFlags.Compressed }],
        };

        var error = Assert.Throws<NtfsAssemblyException>(() =>
            assembler.Assemble(baseRecord, [extension])
        );

        Assert.Equal(NtfsAssemblyFailureKind.Unsupported, error.Kind);
        Assert.Contains("flags mismatch", error.Message);
        Assert.Contains("lowest-vcn=2", error.Message);
        Assert.Contains("highest-vcn=3", error.Message);
    }

    [Fact]
    public void Assemble_OrphanContinuation_Rejects()
    {
        var record = parser.Parse(
            RecordFixture.Record([RecordFixture.NonResident(lowestVcn: 2, highestVcn: 3)]),
            42
        );
        Assert.Throws<NtfsAssemblyException>(() => assembler.Assemble(record, []));
    }

    [Fact]
    public void Assemble_MixedResidentAndNonresidentData_Rejects()
    {
        var record = parser.Parse(
            RecordFixture.Record([
                RecordFixture.NonResident(),
                RecordFixture.Resident(0x80, 2, [1]),
            ]),
            42
        );
        Assert.Throws<NtfsAssemblyException>(() => assembler.Assemble(record, []));
    }

    [Fact]
    public void Assemble_DeletedOrExtensionAsBase_Rejects()
    {
        var (_, extension) = ExtendedFile();
        Assert.Throws<NtfsAssemblyException>(() => assembler.Assemble(extension, []));
        Assert.Throws<NtfsAssemblyException>(() =>
            assembler.Assemble(parser.Parse(RecordFixture.Record(flags: 0), 42), [])
        );
    }

    [Fact]
    public void Assemble_EmptyFile_IsValidZeroLengthStream()
    {
        var record = parser.Parse(
            RecordFixture.Record([
                RecordFixture.NonResident(highestVcn: -1, runs: [0], logical: 0, allocated: 0),
            ]),
            42
        );
        Assert.Equal(0, Assert.Single(assembler.Assemble(record, []).DataStreams).LogicalBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(513)]
    [InlineData(4194304)]
    public void Constructor_InvalidClusterSize_Rejects(int size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MftFileAssembler(size));

    [Theory]
    [InlineData(9000, 8192)]
    [InlineData(5000, 16384)]
    public void Assemble_StreamSizesDisagreeWithMappedClusters_Rejects(long logical, long allocated)
    {
        var record = parser.Parse(
            RecordFixture.Record([
                RecordFixture.NonResident(logical: logical, allocated: allocated),
            ]),
            42
        );
        Assert.Throws<NtfsAssemblyException>(() => assembler.Assemble(record, []));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public void Constructor_InvalidVolumeClusterCount_Rejects(long count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MftFileAssembler(4096, count));

    [Theory]
    [InlineData(0, 2)]
    [InlineData(100, 102)]
    public void Assemble_PhysicalRunEndsAtVolumeBoundary_Accepts(byte lcn, long count)
    {
        var record = parser.Parse(
            RecordFixture.Record([RecordFixture.NonResident(runs: [0x11, 2, lcn, 0])]),
            42
        );

        var file = new MftFileAssembler(4096, count).Assemble(record, []);

        Assert.Equal(8192, Assert.Single(file.DataStreams).AllocatedBytes);
    }

    [Theory]
    [InlineData(100, 101)]
    [InlineData(102, 102)]
    [InlineData(103, 102)]
    public void Assemble_PhysicalRunCrossesOrStartsBeyondVolumeBoundary_Rejects(
        byte lcn,
        long count
    )
    {
        var record = parser.Parse(
            RecordFixture.Record([RecordFixture.NonResident(runs: [0x11, 2, lcn, 0])]),
            42
        );

        var error = Assert.Throws<NtfsAssemblyException>(() =>
            new MftFileAssembler(4096, count).Assemble(record, [])
        );

        Assert.Equal(NtfsAssemblyFailureKind.InvalidStructure, error.Kind);
        Assert.Contains("volume bounds", error.Message);
    }

    [Fact]
    public void Assemble_SparseLogicalRangeExceedsVolume_ValidatesOnlyPhysicalClusters()
    {
        var record = parser.Parse(
            RecordFixture.Record([
                RecordFixture.NonResident(
                    highestVcn: 9,
                    flags: NtfsAttributeFlags.Sparse,
                    runs: [0x11, 1, 0, 0x01, 9, 0],
                    logical: 40960,
                    allocated: 40960,
                    physical: 4096
                ),
            ]),
            42
        );

        var file = new MftFileAssembler(4096, 1).Assemble(record, []);

        var stream = Assert.Single(file.DataStreams);
        Assert.Equal(40960, stream.LogicalBytes);
        Assert.Equal(4096, stream.AllocatedBytes);
    }

    [Fact]
    public void Assemble_NamedDataOutsideVolume_RejectsWholeFile()
    {
        var record = parser.Parse(
            RecordFixture.Record([
                RecordFixture.Resident(0x80, 0, [1]),
                RecordFixture.NonResident(name: "extra"),
            ]),
            42
        );

        Assert.Throws<NtfsAssemblyException>(() =>
            new MftFileAssembler(4096, 101).Assemble(record, [])
        );
    }

    [Fact]
    public void Assemble_ContinuationOutsideVolume_RejectsWholeFile()
    {
        var (baseRecord, extension) = ExtendedFile();
        var attribute = Assert.IsType<NonResidentAttribute>(Assert.Single(extension.Attributes));
        extension = extension with { Attributes = [attribute with { Runs = [new(2, 2, 101)] }] };

        Assert.Throws<NtfsAssemblyException>(() =>
            new MftFileAssembler(4096, 102).Assemble(baseRecord, [extension])
        );
    }

    [Fact]
    public void Assemble_LargestSupportedVolume_UsesOverflowSafeEndCheck()
    {
        const long count = long.MaxValue / 4096;
        var record = parser.Parse(RecordFixture.Record([RecordFixture.NonResident()]), 42);
        var attribute = Assert.IsType<NonResidentAttribute>(Assert.Single(record.Attributes));
        record = record with { Attributes = [attribute with { Runs = [new(0, 2, count - 2)] }] };
        var boundedAssembler = new MftFileAssembler(4096, count);

        Assert.Equal(
            8192,
            Assert.Single(boundedAssembler.Assemble(record, []).DataStreams).AllocatedBytes
        );
        record = record with
        {
            Attributes = [attribute with { Runs = [new(0, 2, long.MaxValue)] }],
        };
        Assert.Throws<NtfsAssemblyException>(() => boundedAssembler.Assemble(record, []));
    }
}
