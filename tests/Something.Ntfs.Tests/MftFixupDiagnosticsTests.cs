using Something.Ntfs;

namespace Something.Ntfs.Tests;

public sealed class MftFixupDiagnosticsTests
{
    [Fact]
    public void Inspect_ProtectedRecord_CountsWithoutChangingInput()
    {
        byte[] bytes = RecordFixture.Record();
        byte[] original = bytes.ToArray();
        var result = Assert.IsType<MftFixupSummary>(MftFixupDiagnostics.Inspect(bytes));
        Assert.Equal(2, result.SectorCount);
        Assert.Equal(2, result.ProtectedTailMatches);
        Assert.Equal(0, result.SavedTailMatches);
        Assert.Equal(0, result.OtherTails);
        Assert.Equal(original, bytes);
    }

    [Fact]
    public void Inspect_RestoredRecord_CountsAllSavedMatches()
    {
        var result = Assert.IsType<MftFixupSummary>(MftFixupDiagnostics.Inspect(Restored()));
        Assert.Equal(2, result.SavedTailMatches);
        Assert.Equal(0, result.OtherTails);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inspect_ModifiedTail_DistinguishesUsedAreaAndPadding(bool inside)
    {
        byte[] bytes = Restored();
        if (inside)
            RecordFixture.U32(bytes, 24, 600);
        bytes[510] ^= 1;
        var result = Assert.IsType<MftFixupSummary>(MftFixupDiagnostics.Inspect(bytes));
        Assert.Equal(1, result.OtherTails);
        Assert.Equal(inside ? 1 : 0, result.SavedMismatchesWithinUsedBytes);
        Assert.Equal(inside ? 0 : 1, result.SavedMismatchesOutsideUsedBytes);
    }

    [Fact]
    public void Inspect_SequenceAlsoEqualsSavedWord_DoesNotChooseEncoding()
    {
        byte[] bytes = Restored();
        RecordFixture.U16(bytes, 48, 0);
        var result = Assert.IsType<MftFixupSummary>(MftFixupDiagnostics.Inspect(bytes));
        Assert.Equal(2, result.SavedTailMatches);
        Assert.Equal(2, result.ProtectedTailMatches);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void Inspect_InvalidHeader_ReturnsNoSummary(int scenario)
    {
        byte[] bytes = RecordFixture.Record();
        switch (scenario)
        {
            case 0:
                bytes = new byte[511];
                break;
            case 1:
                bytes = new byte[65537];
                break;
            case 2:
                bytes = new byte[513];
                break;
            case 3:
                bytes[0] = 0;
                break;
            case 4:
                RecordFixture.U16(bytes, 6, 4);
                break;
            case 5:
                RecordFixture.U16(bytes, 4, 40);
                break;
            case 6:
                RecordFixture.U16(bytes, 4, 49);
                break;
            case 7:
                RecordFixture.U16(bytes, 4, 508);
                break;
            case 8:
                RecordFixture.U32(bytes, 24, 32);
                break;
            default:
                RecordFixture.U32(bytes, 24, 1025);
                break;
        }
        Assert.Null(MftFixupDiagnostics.Inspect(bytes));
    }

    [Fact]
    public void Inspect_DeterministicMutation_NeverReadsBeyondInput()
    {
        var random = new Random(9071);
        for (int n = 0; n < 500; n++)
        {
            byte[] bytes = RecordFixture.Record();
            bytes[random.Next(bytes.Length)] ^= (byte)random.Next(1, 256);
            var summary = MftFixupDiagnostics.Inspect(bytes);
            if (summary is not null)
            {
                Assert.Equal(2, summary.SectorCount);
                Assert.InRange(summary.OtherTails, 0, 2);
            }
        }
    }

    private static byte[] Restored()
    {
        byte[] bytes = RecordFixture.Record();
        for (int n = 1; n <= 2; n++)
            bytes.AsSpan(48 + n * 2, 2).CopyTo(bytes.AsSpan(n * 512 - 2, 2));
        return bytes;
    }
}
