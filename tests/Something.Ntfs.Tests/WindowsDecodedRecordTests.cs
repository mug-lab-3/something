namespace Something.Ntfs.Tests;

public sealed class WindowsDecodedRecordTests
{
    [Fact]
    public void Parse_WindowsDecoded_UsesCurrentResidentBytesWithoutRestoringStaleSavedWords()
    {
        byte[] bytes = DecodedWithStaleSavedWord();
        byte[] original = bytes.ToArray();
        var parser = new MftRecordParser();
        var record = parser.Parse(bytes, 42, MftRecordEncoding.WindowsDecoded);
        var attribute = Assert.IsType<ResidentAttribute>(Assert.Single(record.Attributes));
        Assert.Equal(0x22, attribute.Value[430]);
        Assert.Equal(original, bytes);
        Assert.Throws<NtfsParseException>(() => parser.Parse(bytes, 42));
        Assert.Throws<NtfsParseException>(() =>
            parser.Parse(bytes, 42, MftRecordEncoding.FixupsRestored)
        );
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
    public void Parse_WindowsDecoded_InvalidStructureStillRejects(int scenario)
    {
        byte[] bytes = DecodedWithStaleSavedWord();
        switch (scenario)
        {
            case 0:
                bytes[0] = 0;
                break;
            case 1:
                RecordFixture.U16(bytes, 6, 4);
                break;
            case 2:
                RecordFixture.U16(bytes, 4, 508);
                break;
            case 3:
                RecordFixture.U16(bytes, 4, 49);
                break;
            case 4:
                RecordFixture.U32(bytes, 24, 1025);
                break;
            case 5:
                RecordFixture.U32(bytes, 28, 512);
                break;
            case 6:
                RecordFixture.U32(bytes, 60, 0xFFFFFFF8);
                break;
            default:
                RecordFixture.U32(bytes, 72, uint.MaxValue);
                break;
        }
        Assert.Throws<NtfsParseException>(() =>
            new MftRecordParser().Parse(bytes, 42, MftRecordEncoding.WindowsDecoded)
        );
    }

    [Fact]
    public async Task ReadAsync_WindowsDecoded_StillDetectsChangesAndReferenceReuse()
    {
        var source = new Source(
            (_, n) =>
            {
                byte[] bytes = DecodedWithStaleSavedWord();
                bytes[510] = n == 1 ? (byte)0x23 : (byte)0x22;
                return bytes;
            }
        );
        var result = await new LiveMftRecordReader().ReadAsync(
            source,
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Equal(LiveRecordReadStatus.ObservedUnchanged, result.Status);
        Assert.Equal(2, result.Attempts);
        var reused = new Source(
            (_, _) =>
            {
                byte[] bytes = DecodedWithStaleSavedWord();
                RecordFixture.U16(bytes, 16, 4);
                return bytes;
            }
        );
        var replacement = await new LiveMftRecordReader().ReadAsync(
            reused,
            42,
            FileReference.Create(42, 3),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(LiveRecordReadStatus.Replaced, replacement.Status);
        Assert.Null(replacement.Record);
    }

    [Fact]
    public async Task ReadAsync_WindowsDecoded_AssemblesResidentDataFromCurrentBytes()
    {
        var source = new Source((_, _) => DecodedWithStaleSavedWord());
        var result = await new LiveMftFileReader(4096).ReadAsync(
            source,
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        Assert.Equal(600, Assert.Single(result.File!.DataStreams).LogicalBytes);
        var attribute = Assert.IsType<ResidentAttribute>(Assert.Single(result.File.Attributes));
        Assert.Equal(0x22, attribute.Value[430]);
    }

    [Fact]
    public void Parse_WindowsDecoded_DeterministicMutation_OnlyReturnsOwnedModelsOrParseErrors()
    {
        var random = new Random(7061);
        var parser = new MftRecordParser();
        for (int n = 0; n < 500; n++)
        {
            byte[] bytes = DecodedWithStaleSavedWord();
            bytes[random.Next(bytes.Length)] ^= (byte)random.Next(1, 256);
            byte[] original = bytes.ToArray();
            if (
                parser.TryParse(
                    bytes,
                    42,
                    out var record,
                    out var error,
                    MftRecordEncoding.WindowsDecoded
                )
            )
            {
                Assert.NotNull(record);
                Assert.Null(error);
            }
            else
                Assert.NotNull(error);
            Assert.Equal(original, bytes);
        }
    }

    private static byte[] DecodedWithStaleSavedWord()
    {
        byte[] bytes = RecordFixture.Record([
            RecordFixture.Resident(0x80, 0, Enumerable.Repeat((byte)0x11, 600).ToArray()),
        ]);
        for (int n = 1; n <= 2; n++)
            bytes.AsSpan(48 + n * 2, 2).CopyTo(bytes.AsSpan(n * 512 - 2, 2));
        // Simulate a current cached resident value whose saved pre-write tail is older.
        bytes[510] = 0x22;
        return bytes;
    }

    private sealed class Source(Func<ulong, int, byte[]> read) : IMftRecordSource
    {
        private int calls;
        public MftRecordEncoding Encoding => MftRecordEncoding.WindowsDecoded;

        public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
            ulong recordIndex,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(read(recordIndex, ++calls));
    }
}
