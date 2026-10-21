namespace Something.Ntfs.Tests;

// MFT is a test-only oracle. Agreement is supporting evidence, never a correctness proof.
public sealed class OssComparisonTests
{
    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(4096)]
    public void ValidSyntheticRecords_AgreeWithEricZimmermanMftOnIdentityAndDataSizes(int size)
    {
        byte[] raw = RecordFixture.Record(size: size);
        var ours = new MftRecordParser().Parse(raw, 42);
        var oracle = new MFT.FileRecord((byte[])raw.Clone(), 0, recoverFromSlack: false);
        Assert.True(oracle.FixupOk);
        Assert.Equal(ours.Reference.SequenceNumber, oracle.SequenceNumber);
        Assert.Equal(ours.Reference.RecordIndex, oracle.EntryNumber);
        var data = Assert.Single(oracle.Attributes.OfType<MFT.Attributes.Data>());
        var oursData = Assert.IsType<NonResidentAttribute>(ours.Attributes[1]);
        Assert.Equal((ulong)oursData.Sizes!.LogicalBytes, data.NonResidentData.ActualSize);
        Assert.Equal((ulong)oursData.Sizes.AllocatedBytes, data.NonResidentData.AllocatedSize);
    }

    [Fact]
    public void TornRecord_OurStrictApiRejectsWhatOracleMarksWithFixupFlag()
    {
        byte[] raw = RecordFixture.Record();
        raw[510] ^= 1;
        Assert.Throws<NtfsParseException>(() => new MftRecordParser().Parse(raw, 42));
        Assert.False(new MFT.FileRecord((byte[])raw.Clone(), 0, false).FixupOk);
    }
}
