using Something.Ntfs;

namespace Something.Ntfs.Tests;

public sealed class MftFileAssemblerTests
{
    private static readonly FileReference BaseReference = FileReference.Create(42, 3);
    private static readonly FileReference ExtensionReference = FileReference.Create(43, 3);
    private readonly MftRecordParser parser = new();
    private readonly MftFileAssembler assembler = new(4096);

    private (MftRecord Base, MftRecord Extension) ExtendedFile(bool nonresidentList = false)
    {
        byte[] list = [.. RecordFixture.ListEntry(0x80, 1, BaseReference),
            .. RecordFixture.ListEntry(0x80, 0, ExtensionReference, 2)];
        byte[] listAttribute = nonresidentList
            ? RecordFixture.NonResident(id: 0, type: 0x20, logical: list.Length)
            : RecordFixture.Resident(0x20, 0, list);
        var baseRecord = parser.Parse(RecordFixture.Record([listAttribute, RecordFixture.NonResident(allocated: 16384)]), 42);
        var extension = parser.Parse(RecordFixture.Record([
            RecordFixture.NonResident(id: 7, lowestVcn: 2, highestVcn: 3)], baseReference: BaseReference.Value, index: 43), 43);
        return (baseRecord, extension);
    }

    [Fact]
    public void Assemble_ExtendedDataStream_CombinesRunsWithoutDoubleCountingSizes()
    {
        var (baseRecord, extension) = ExtendedFile();
        var file = assembler.Assemble(baseRecord, [extension]);
        var stream = Assert.Single(file.DataStreams);
        Assert.Equal(5000, stream.LogicalBytes); Assert.Equal(16384, stream.AllocatedBytes);
        Assert.Equal(2, stream.Runs.Length); Assert.Equal(2, stream.Runs[1].StartVcn);
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
        Assert.Throws<NtfsAssemblyException>(() => assembler.Assemble(baseRecord, [extension, extension]));
    }

    [Fact]
    public void Assemble_NonresidentList_RequiresCompleteSuppliedValue()
    {
        var (baseRecord, extension) = ExtendedFile(nonresidentList: true);
        Assert.Throws<NtfsAssemblyException>(() => assembler.Assemble(baseRecord, [extension]));
        byte[] list = [.. RecordFixture.ListEntry(0x80, 1, BaseReference),
            .. RecordFixture.ListEntry(0x80, 0, ExtensionReference, 2)];
        Assert.Single(assembler.Assemble(baseRecord, [extension], list).DataStreams);
        Assert.Throws<NtfsAssemblyException>(() => assembler.Assemble(baseRecord, [extension], list.AsMemory(0, list.Length - 1)));
    }

    [Theory]
    [InlineData(1, 2)] [InlineData(3, 4)]
    public void Assemble_OverlappingOrGappedExtents_Rejects(long lowest, long highest)
    {
        var record = parser.Parse(RecordFixture.Record([RecordFixture.NonResident(),
            RecordFixture.NonResident(id: 2, lowestVcn: lowest, highestVcn: highest)]), 42);
        Assert.Throws<NtfsAssemblyException>(() => assembler.Assemble(record, []));
    }

    [Fact]
    public void Assemble_OrphanContinuation_Rejects()
    {
        var record = parser.Parse(RecordFixture.Record([RecordFixture.NonResident(lowestVcn: 2, highestVcn: 3)]), 42);
        Assert.Throws<NtfsAssemblyException>(() => assembler.Assemble(record, []));
    }

    [Fact]
    public void Assemble_MixedResidentAndNonresidentData_Rejects()
    {
        var record = parser.Parse(RecordFixture.Record([RecordFixture.NonResident(), RecordFixture.Resident(0x80, 2, [1])]), 42);
        Assert.Throws<NtfsAssemblyException>(() => assembler.Assemble(record, []));
    }

    [Fact]
    public void Assemble_DeletedOrExtensionAsBase_Rejects()
    {
        var (_, extension) = ExtendedFile();
        Assert.Throws<NtfsAssemblyException>(() => assembler.Assemble(extension, []));
        Assert.Throws<NtfsAssemblyException>(() => assembler.Assemble(parser.Parse(RecordFixture.Record(flags: 0), 42), []));
    }

    [Fact]
    public void Assemble_EmptyFile_IsValidZeroLengthStream()
    {
        var record = parser.Parse(RecordFixture.Record([RecordFixture.NonResident(highestVcn: -1, runs: [0], logical: 0, allocated: 0)]), 42);
        Assert.Equal(0, Assert.Single(assembler.Assemble(record, []).DataStreams).LogicalBytes);
    }

    [Theory]
    [InlineData(0)] [InlineData(513)] [InlineData(4194304)]
    public void Constructor_InvalidClusterSize_Rejects(int size)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new MftFileAssembler(size));

    [Theory]
    [InlineData(9000, 8192)] [InlineData(5000, 16384)]
    public void Assemble_StreamSizesDisagreeWithMappedClusters_Rejects(long logical, long allocated)
    {
        var record = parser.Parse(RecordFixture.Record([RecordFixture.NonResident(logical: logical, allocated: allocated)]), 42);
        Assert.Throws<NtfsAssemblyException>(() => assembler.Assemble(record, []));
    }
}
