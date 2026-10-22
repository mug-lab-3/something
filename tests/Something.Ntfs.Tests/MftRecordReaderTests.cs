namespace Something.Ntfs.Tests;

public sealed class MftRecordReaderTests
{
    [Fact]
    public async Task ReadAsync_PartialReadsAndMixedSlots_PreservesIndicesAndReportsEachOutcome()
    {
        byte[] invalid = RecordFixture.Record();
        invalid[510] ^= 1;
        byte[] bytes =
        [
            .. RecordFixture.Record(),
            .. new byte[1024],
            .. invalid,
            .. RecordFixture.Record(flags: 0),
            1,
            2,
            3,
        ];
        using var stream = new PartialStream(bytes, 7);
        var results = await Collect(
            new MftRecordReader(1024).ReadAsync(stream, 10, TestContext.Current.CancellationToken)
        );
        Assert.Equal(
            new[]
            {
                MftRecordReadStatus.Parsed,
                MftRecordReadStatus.Uninitialized,
                MftRecordReadStatus.Invalid,
                MftRecordReadStatus.Parsed,
                MftRecordReadStatus.Invalid,
            },
            results.Select(r => r.Status)
        );
        Assert.Equal(new ulong[] { 10, 11, 12, 13, 14 }, results.Select(r => r.RecordIndex));
        Assert.Equal(3, results[^1].Error!.Offset);
        Assert.True(stream.CanRead); // caller retains ownership
    }

    [Fact]
    public async Task ReadAsync_EmptyStream_HasNoResults()
    {
        using var stream = new MemoryStream();
        Assert.Empty(
            await Collect(
                new MftRecordReader(1024).ReadAsync(
                    stream,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
        );
    }

    [Fact]
    public async Task ReadAsync_Cancelled_PropagatesCancellation()
    {
        using var stream = new MemoryStream(RecordFixture.Record());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Collect(
                new MftRecordReader(1024).ReadAsync(stream, cancellationToken: cancellation.Token)
            )
        );
    }

    [Fact]
    public async Task ReadAsync_StopEarly_DoesNotCloseStream()
    {
        using var stream = new MemoryStream([.. RecordFixture.Record(), .. RecordFixture.Record()]);
        await foreach (
            var _ in new MftRecordReader(1024).ReadAsync(
                stream,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            break;
        Assert.True(stream.CanRead);
        Assert.Equal(1024, stream.Position);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(511)]
    [InlineData(768)]
    [InlineData(131072)]
    public void Constructor_InvalidRecordSize_Rejects(int size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MftRecordReader(size));

    private static async Task<List<MftRecordReadResult>> Collect(
        IAsyncEnumerable<MftRecordReadResult> source
    )
    {
        List<MftRecordReadResult> results = [];
        await foreach (var result in source)
            results.Add(result);
        return results;
    }

    private sealed class PartialStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }
}
