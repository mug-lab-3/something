using Something.Ntfs;

namespace Something.Ntfs.Tests;

public sealed class DataRunParserTests
{
    [Fact]
    public void Parse_PositiveNegativeAndSparseRuns_UsesRelativeLcnWithoutMovingItForHoles()
    {
        var runs = DataRunParser.Parse([0x11, 2, 100, 0x11, 3, 0xF6, 0x01, 4, 0x11, 1, 5, 0], 0, 9);
        Assert.Equal(new DataRun[] { new(0, 2, 100), new(2, 3, 90), new(5, 4, null), new(9, 1, 95) }, runs);
    }

    [Fact]
    public void Parse_EmptyStream_AllowsNoRuns()
        => Assert.Empty(DataRunParser.Parse([0], 0, -1));

    [Fact]
    public void Parse_LcnZero_IsAllocatedWhenOffsetFieldIsPresent()
        => Assert.Equal(0, Assert.Single(DataRunParser.Parse([0x11, 1, 0, 0], 0, 0)).StartLcn);

    [Fact]
    public void Parse_EightByteDelta_SignExtendsAndChecksOverflow()
    {
        Assert.Throws<NtfsParseException>(() => DataRunParser.Parse([0x81, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0], 0, 0));
        Assert.Throws<NtfsParseException>(() => DataRunParser.Parse([0x81, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 0], 0, 0));
    }

    [Theory]
    [InlineData(0x10)] [InlineData(0x19)] [InlineData(0x91)]
    public void Parse_InvalidFieldWidths_Rejects(byte header)
        => Assert.Throws<NtfsParseException>(() => DataRunParser.Parse([header, 1, 1, 0], 0, 0));

    [Theory]
    [InlineData(-1, 0)] [InlineData(0, -2)] [InlineData(2, 1)] [InlineData(0, long.MaxValue)]
    public void Parse_InvalidVcnRanges_Rejects(long low, long high)
        => Assert.Throws<NtfsParseException>(() => DataRunParser.Parse([0], low, high));

    [Fact]
    public void Parse_TruncatedOrZeroLengthOrUnterminatedRuns_Rejects()
    {
        Assert.Throws<NtfsParseException>(() => DataRunParser.Parse([], 0, 0));
        Assert.Throws<NtfsParseException>(() => DataRunParser.Parse([0x11, 1], 0, 0));
        Assert.Throws<NtfsParseException>(() => DataRunParser.Parse([0x11, 0, 1, 0], 0, 0));
        Assert.Throws<NtfsParseException>(() => DataRunParser.Parse([0x11, 1, 1], 0, 0));
    }

    [Theory]
    [InlineData(0)] [InlineData(2)]
    public void Parse_DeclaredCoverageDiffersFromRuns_Rejects(long highest)
        => Assert.Throws<NtfsParseException>(() => DataRunParser.Parse([0x11, 2, 100, 0], 0, highest));

    [Fact]
    public void Parse_FixtureLikeStaleMftExtentBound_Rejects314ClustersDeclaredAs298()
        => Assert.Throws<NtfsParseException>(() => DataRunParser.Parse([0x12, 0x3A, 0x01, 100, 0], 0, 297));

    [Fact]
    public void Parse_FixtureLikeInflatedExtentBound_RejectsOneClusterDeclaredAs81920()
        => Assert.Throws<NtfsParseException>(() => DataRunParser.Parse([0x11, 1, 100, 0], 0, 81919));

    [Fact]
    public void Parse_UnsignedLengthExceedsInt64_Rejects()
        => Assert.Throws<NtfsParseException>(() => DataRunParser.Parse([0x18, 0, 0, 0, 0, 0, 0, 0, 0x80, 1, 0], 0, 0));

    [Fact]
    public void Parse_RelativeLcnSumOverflows_ReportsStructuredError()
        => Assert.Throws<NtfsParseException>(() => DataRunParser.Parse([
            0x81, 1, 0xFD, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F,
            0x11, 1, 3, 0], 0, 1));

    [Fact]
    public void Parse_VcnSumOverflows_ReportsStructuredError()
        => Assert.Throws<NtfsParseException>(() => DataRunParser.Parse([0x01, 2, 0], long.MaxValue - 1, long.MaxValue - 1));

    [Fact]
    public void Parse_RandomBytes_OnlyReturnsRunsOrStructuredErrors()
    {
        var random = new Random(1234);
        for (int i = 0; i < 5000; i++)
        {
            byte[] bytes = new byte[random.Next(0, 128)]; random.NextBytes(bytes);
            try { DataRunParser.Parse(bytes, 0, 4); }
            catch (NtfsParseException) { }
        }
    }
}
