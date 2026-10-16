using Something.Ntfs;

namespace Something.Ntfs.Tests;

public sealed class AttributeValueParserTests
{
    [Theory]
    [InlineData(48, 0u)]
    [InlineData(72, 6u)]
    [InlineData(72, uint.MaxValue)]
    public void StandardInformation_PreservesAllFlagsWithoutMutatingInput(int length, uint flags)
    {
        byte[] bytes = new byte[length];
        RecordFixture.U32(bytes, 32, flags);
        var original = bytes.ToArray();
        Assert.Equal(flags, AttributeValueParser.ParseStandardFileAttributes(bytes));
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(47)]
    [InlineData(49)]
    [InlineData(71)]
    [InlineData(73)]
    public void StandardInformation_RejectsTruncationAndUnsupportedLengths(int length) =>
        Assert.Throws<NtfsParseException>(() =>
            AttributeValueParser.ParseStandardFileAttributes(new byte[length])
        );

    [Fact]
    public void StandardInformation_RecordParsingAndAssemblyDoNotUseCachedFileNameFlags()
    {
        byte[] standard = new byte[72];
        RecordFixture.U32(standard, 32, 6);
        var bytes = RecordFixture.Record([
            RecordFixture.Resident(0x10, 0, standard),
            RecordFixture.Resident(0x30, 1, RecordFixture.FileName("test")),
        ]);
        var parsed = new MftRecordParser().Parse(bytes, 42);
        var file = new MftFileAssembler(4096).Assemble(parsed, []);
        Assert.Equal(6u, file.StandardFileAttributes);
        Assert.Equal(0u, Assert.Single(file.Names).FileAttributes);
        var missing = new MftRecordParser().Parse(
            RecordFixture.Record([RecordFixture.Resident(0x30, 1, RecordFixture.FileName("test"))]),
            42
        );
        Assert.Null(new MftFileAssembler(4096).Assemble(missing, []).StandardFileAttributes);
    }

    [Fact]
    public void StandardInformation_RejectsNonresidentNamedAndDuplicateAttributes()
    {
        var parser = new MftRecordParser();
        Assert.Throws<NtfsParseException>(() =>
            parser.Parse(RecordFixture.Record([RecordFixture.NonResident(type: 0x10)]), 42)
        );
        Assert.Throws<NtfsParseException>(() =>
            parser.Parse(
                RecordFixture.Record([RecordFixture.Resident(0x10, 0, new byte[72], "named")]),
                42
            )
        );
        var duplicate = parser.Parse(
            RecordFixture.Record([
                RecordFixture.Resident(0x10, 0, new byte[72]),
                RecordFixture.Resident(0x10, 1, new byte[72]),
            ]),
            42
        );
        Assert.Throws<NtfsAssemblyException>(() =>
            new MftFileAssembler(4096).Assemble(duplicate, [])
        );
    }

    [Theory]
    [InlineData("日本語.txt")]
    [InlineData("😀.txt")]
    [InlineData("\uD800.txt")]
    public void ParseFileName_PreservesUtf16CodeUnits(string name) =>
        Assert.Equal(name, AttributeValueParser.ParseFileName(RecordFixture.FileName(name)).Name);

    [Fact]
    public void ParseFileName_PreservesParentSequence() =>
        Assert.Equal(
            FileReference.Create(5, 2),
            AttributeValueParser.ParseFileName(RecordFixture.FileName("x")).Parent
        );

    [Theory]
    [InlineData(64, 0)]
    [InlineData(64, 255)]
    [InlineData(65, 4)]
    public void ParseFileName_InvalidNameMetadata_Rejects(int offset, byte value)
    {
        byte[] bytes = RecordFixture.FileName("x");
        bytes[offset] = value;
        Assert.Throws<NtfsParseException>(() => AttributeValueParser.ParseFileName(bytes));
    }

    [Fact]
    public void ParseAttributeList_PreservesNamesVcnAndReferences()
    {
        var reference = FileReference.Create(200, 7);
        var entry = Assert.Single(
            AttributeValueParser.ParseAttributeList(
                RecordFixture.ListEntry(0x80, 2, reference, 5, "ads")
            )
        );
        Assert.Equal(reference, entry.Record);
        Assert.Equal(5, entry.LowestVcn);
        Assert.Equal("ads", entry.Name);
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(4, 26)]
    [InlineData(4, 255)]
    [InlineData(7, 0)]
    [InlineData(7, 27)]
    [InlineData(7, 254)]
    public void ParseAttributeList_InvalidEntryBoundaries_Rejects(int offset, byte value)
    {
        byte[] bytes = RecordFixture.ListEntry(0x80, 2, FileReference.Create(200, 7), name: "ads");
        bytes[offset] = value;
        Assert.Throws<NtfsParseException>(() => AttributeValueParser.ParseAttributeList(bytes));
    }

    [Fact]
    public void ParseAttributeList_EmptyValue_IsEmpty() =>
        Assert.Empty(AttributeValueParser.ParseAttributeList([]));

    [Fact]
    public void FileReference_PreservesFull48BitIndexAnd16BitSequence()
    {
        var reference = FileReference.Create(FileReference.MaximumRecordIndex, ushort.MaxValue);
        Assert.Equal(ulong.MaxValue, reference.Value);
        Assert.Equal(FileReference.MaximumRecordIndex, reference.RecordIndex);
        Assert.Equal(ushort.MaxValue, reference.SequenceNumber);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FileReference.Create(FileReference.MaximumRecordIndex + 1, 0)
        );
    }
}
