using Something.Ntfs;

namespace Something.Ntfs.Tests;

public sealed class AttributeValueParserTests
{
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
