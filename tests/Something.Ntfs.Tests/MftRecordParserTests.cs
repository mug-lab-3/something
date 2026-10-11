using Something.Ntfs;

namespace Something.Ntfs.Tests;

public sealed class MftRecordParserTests
{
    private readonly MftRecordParser parser = new();

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(255)]
    public void Parse_CompressionUnitAndReservedBytes_AreSeparateOwnedFields(byte unit)
    {
        byte[] attribute = RecordFixture.NonResident();
        attribute[34] = unit;
        byte[] reserved = [0xFF, 0x91, 0x52, 0x33, 0x14];
        reserved.CopyTo(attribute, 35);
        byte[] bytes = RecordFixture.Record([attribute]);
        byte[] original = bytes.ToArray();

        var parsed = Assert.IsType<NonResidentAttribute>(
            Assert.Single(parser.Parse(bytes, 42).Attributes)
        );

        Assert.Equal(unit, parsed.CompressionUnit);
        Assert.Equal(reserved, parsed.ReservedHeaderBytes);
        Assert.Equal(original, bytes);
        Array.Clear(bytes);
        Assert.Equal(reserved, parsed.ReservedHeaderBytes);
    }

    [Fact]
    public void Parse_ValidRecord_PreservesIdentityAndCurrentDataSizes()
    {
        byte[] bytes = RecordFixture.Record();
        MftRecord record = parser.Parse(bytes, 42);
        Assert.Equal(FileReference.Create(42, 3), record.Reference);
        Assert.True(record.IsInUse);
        Assert.False(record.IsExtension);
        var file = new MftFileAssembler(4096).Assemble(record, []);
        Assert.Equal("hello.txt", Assert.Single(file.Names).Name);
        Assert.Equal(5000, Assert.Single(file.DataStreams).LogicalBytes);
        Assert.Equal(8192, file.DataStreams[0].AllocatedBytes);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(4096)]
    [InlineData(65536)]
    public void Parse_SupportedRecordSizes_RestoresEvery512ByteTail(int size)
    {
        byte[] value = Enumerable.Repeat((byte)0x71, size == 512 ? 100 : 600).ToArray();
        byte[] bytes = RecordFixture.Record(
            [RecordFixture.Resident(NtfsAttributeTypes.Data, 0, value)],
            size
        );
        var record = parser.Parse(bytes, 42);
        Assert.Equal(
            value,
            Assert.IsType<ResidentAttribute>(Assert.Single(record.Attributes)).Value
        );
    }

    [Fact]
    public void Parse_DoesNotMutateInputOrRetainPooledMemory()
    {
        byte[] bytes = RecordFixture.Record();
        byte[] original = (byte[])bytes.Clone();
        var record = parser.Parse(bytes, 42);
        Assert.Equal(original, bytes);
        Array.Clear(bytes);
        for (int i = 0; i < 20; i++)
            parser.Parse(RecordFixture.Record(), 50);
        Assert.Equal(
            "hello.txt",
            AttributeValueParser
                .ParseFileName(((ResidentAttribute)record.Attributes[0]).Value.AsSpan())
                .Name
        );
    }

    [Fact]
    public void Parse_PreservesHardLinksDosAliasesAndNamedStreams()
    {
        byte[] bytes = RecordFixture.Record([
            RecordFixture.Resident(
                NtfsAttributeTypes.FileName,
                0,
                RecordFixture.FileName("long-name.txt")
            ),
            RecordFixture.Resident(
                NtfsAttributeTypes.FileName,
                1,
                RecordFixture.FileName("LONG-N~1.TXT", FileNameNamespace.Dos)
            ),
            RecordFixture.Resident(
                NtfsAttributeTypes.FileName,
                2,
                RecordFixture.FileName("other-link.txt", parent: 99)
            ),
            RecordFixture.Resident(NtfsAttributeTypes.Data, 3, [1, 2, 3]),
            RecordFixture.Resident(NtfsAttributeTypes.Data, 4, [4, 5], "Zone.Identifier"),
        ]);
        var file = new MftFileAssembler(4096).Assemble(parser.Parse(bytes, 42), []);
        Assert.Equal(3, file.Names.Length);
        Assert.Equal(2, file.DataStreams.Length);
        Assert.Equal(0, file.DataStreams[0].AllocatedBytes);
        Assert.Equal("Zone.Identifier", file.DataStreams[1].Name);
    }

    [Fact]
    public void Parse_UnknownAttribute_PreservesValueWithoutAssumingItsMeaning()
    {
        var record = parser.Parse(
            RecordFixture.Record([RecordFixture.Resident(0x1234, 0, [1, 2, 3])]),
            42
        );
        var attribute = Assert.IsType<ResidentAttribute>(Assert.Single(record.Attributes));
        Assert.Equal(0x1234u, attribute.Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, attribute.Value);
    }

    [Fact]
    public void Parse_DeletedRecord_DoesNotExposeStaleAttributes()
    {
        byte[] bytes = RecordFixture.Record(flags: 0);
        bytes[56] = 0; // stale attribute chain would otherwise be invalid
        var record = parser.Parse(bytes, 42);
        Assert.False(record.IsInUse);
        Assert.Empty(record.Attributes);
    }

    [Theory]
    [InlineData((MftRecordFlags)0)]
    [InlineData(MftRecordFlags.Directory)]
    public void Parse_UnusedRecordWithMismatchedFixup_StillReportsStructuralFailure(
        MftRecordFlags flags
    )
    {
        byte[] bytes = RecordFixture.Record(flags: flags);
        bytes[510] ^= 1;
        Assert.Throws<NtfsParseException>(() => parser.Parse(bytes, 42));
    }

    [Fact]
    public void Parse_BaadMarker_IsNotAcceptedAsAValidOrEmptyRecord()
    {
        byte[] bytes = new byte[1024];
        "BAAD"u8.CopyTo(bytes);
        var error = Assert.Throws<NtfsParseException>(() => parser.Parse(bytes, 42));
        Assert.Equal(0, error.Offset);
    }

    [Fact]
    public void Parse_AttributesReachUsedSizeWithoutEndMarker_Rejects()
    {
        byte[] bytes = RecordFixture.Record([RecordFixture.NonResident()]);
        // first attribute starts at 56 and is 72 bytes long. End marker is outside used bytes.
        RecordFixture.U32(bytes, 24, 56 + 72);
        Assert.Throws<NtfsParseException>(() => parser.Parse(bytes, 42));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(48)]
    [InlineData(513)]
    [InlineData(768)]
    [InlineData(131072)]
    public void Parse_InvalidRecordLength_ReportsStructuredError(int size) =>
        Assert.Throws<NtfsParseException>(() => parser.Parse(new byte[size], 42));

    [Theory]
    [InlineData(510)]
    [InlineData(1022)]
    public void Parse_TornSector_ReportsExactOffset(int tail)
    {
        byte[] bytes = RecordFixture.Record();
        bytes[tail] ^= 1;
        var error = Assert.Throws<NtfsParseException>(() => parser.Parse(bytes, 42));
        Assert.Equal(tail, error.Offset);
    }

    [Theory]
    [InlineData(4, 40)]
    [InlineData(4, 49)]
    [InlineData(4, 510)]
    [InlineData(6, 0)]
    [InlineData(6, 2)]
    [InlineData(6, 65535)]
    [InlineData(20, 48)]
    [InlineData(20, 57)]
    [InlineData(20, 65528)]
    public void Parse_InvalidHeaderOffsetsOrCounts_RejectsRecord(int offset, ushort value)
    {
        byte[] bytes = RecordFixture.Record();
        RecordFixture.U16(bytes, offset, value);
        Assert.Throws<NtfsParseException>(() => parser.Parse(bytes, 42));
    }

    [Theory]
    [InlineData(24, 0u)]
    [InlineData(24, 1025u)]
    [InlineData(24, uint.MaxValue)]
    [InlineData(28, 512u)]
    [InlineData(28, uint.MaxValue)]
    [InlineData(60, 0u)]
    [InlineData(60, 8u)]
    [InlineData(60, 25u)]
    [InlineData(60, uint.MaxValue)]
    public void Parse_InvalidLengthFields_RejectsRecord(int offset, uint value)
    {
        byte[] bytes = RecordFixture.Record();
        RecordFixture.U32(bytes, offset, value);
        Assert.Throws<NtfsParseException>(() => parser.Parse(bytes, 42));
    }

    [Fact]
    public void Parse_MissingEndMarker_DoesNotReadBeyondUsedBytes()
    {
        byte[] bytes = RecordFixture.Record([]);
        RecordFixture.U32(bytes, 56, 0x80);
        Assert.Throws<NtfsParseException>(() => parser.Parse(bytes, 42));
    }

    [Fact]
    public void Parse_DuplicateInstanceIds_RejectsRecord()
    {
        byte[] bytes = RecordFixture.Record([
            RecordFixture.Resident(0x1234, 1, []),
            RecordFixture.Resident(0x1235, 1, []),
        ]);
        Assert.Throws<NtfsParseException>(() => parser.Parse(bytes, 42));
    }

    [Theory]
    [InlineData(8, 2)]
    [InlineData(9, 255)]
    [InlineData(20, 0)]
    public void Parse_InvalidResidentFields_RejectsRecord(int field, byte value)
    {
        byte[] attribute = RecordFixture.Resident(NtfsAttributeTypes.Data, 0, [1]);
        attribute[field] = value;
        Assert.Throws<NtfsParseException>(() =>
            parser.Parse(RecordFixture.Record([attribute]), 42)
        );
    }

    [Fact]
    public void Parse_ContinuationExtent_DoesNotUseUndefinedSizeFields()
    {
        byte[] attribute = RecordFixture.NonResident(
            lowestVcn: 2,
            highestVcn: 3,
            logical: -1,
            allocated: -1
        );
        var parsed = Assert.IsType<NonResidentAttribute>(
            Assert.Single(parser.Parse(RecordFixture.Record([attribute]), 42).Attributes)
        );
        Assert.Null(parsed.Sizes);
        Assert.Equal(2, parsed.Runs[0].StartVcn);
    }

    [Theory]
    [InlineData(NtfsAttributeFlags.Sparse)]
    [InlineData(NtfsAttributeFlags.Compressed)]
    public void Parse_CompressedOrSparseContinuation_Accepts64ByteHeader(NtfsAttributeFlags flags)
    {
        byte[] attribute = RecordFixture.NonResident(lowestVcn: 2, highestVcn: 3, flags: flags);
        var parsed = Assert.IsType<NonResidentAttribute>(
            Assert.Single(parser.Parse(RecordFixture.Record([attribute]), 42).Attributes)
        );
        Assert.Null(parsed.Sizes);
        Assert.Equal(2, parsed.Runs[0].StartVcn);
    }

    [Theory]
    [InlineData(NtfsAttributeFlags.Sparse)]
    [InlineData(NtfsAttributeFlags.Compressed)]
    public void Parse_SparseOrCompressed_UsesPhysicalAllocationField(NtfsAttributeFlags flags)
    {
        byte[] attribute = RecordFixture.NonResident(
            flags: flags,
            physical: 4096,
            runs: [0x11, 1, 100, 0x01, 1, 0]
        );
        var file = new MftFileAssembler(4096).Assemble(
            parser.Parse(RecordFixture.Record([attribute]), 42),
            []
        );
        Assert.Equal(4096, Assert.Single(file.DataStreams).AllocatedBytes);
    }

    [Theory]
    [InlineData(40, -1)]
    [InlineData(48, -1)]
    [InlineData(56, -1)]
    [InlineData(56, 999999)]
    public void Parse_InvalidNonResidentSizes_RejectsRecord(int field, long value)
    {
        byte[] attribute = RecordFixture.NonResident();
        RecordFixture.I64(attribute, field, value);
        Assert.Throws<NtfsParseException>(() =>
            parser.Parse(RecordFixture.Record([attribute]), 42)
        );
    }

    [Fact]
    public void TryParse_ReturnsEitherOwnedResultOrStructuredError()
    {
        Assert.True(parser.TryParse(RecordFixture.Record(), 42, out var parsed, out var error));
        Assert.NotNull(parsed);
        Assert.Null(error);
        Assert.False(parser.TryParse(new byte[1024], 42, out parsed, out error));
        Assert.Null(parsed);
        Assert.NotNull(error);
    }

    [Fact]
    public void Parse_IdentityOutside48BitRange_IsCallerError() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            parser.Parse(RecordFixture.Record(), FileReference.MaximumRecordIndex + 1)
        );

    [Fact]
    public void TryParse_DeterministicMutations_NeverLeaksBoundsOrOverflowExceptions()
    {
        var random = new Random(0x4D4654);
        byte[] valid = RecordFixture.Record();
        for (int i = 0; i < 5000; i++)
        {
            byte[] bytes = (byte[])valid.Clone();
            for (int j = 0; j < 1 + i % 8; j++)
                bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            bool success = parser.TryParse(bytes, 42, out var record, out var error);
            Assert.Equal(success, record is not null);
            Assert.Equal(!success, error is not null);
            if (error is not null)
                Assert.InRange(error.Offset, 0, bytes.Length);
        }
    }
}
