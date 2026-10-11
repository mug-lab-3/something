using Something.Ntfs;

namespace Something.Ntfs.Tests;

public sealed class LiveMftRecordReaderTests
{
    [Fact]
    public async Task ReadAsync_UnchangedRecord_ReportsObservationWithoutSnapshotGuarantee()
    {
        byte[] bytes = RecordFixture.Record();
        var source = new SequenceSource(bytes, bytes);
        var result = await new LiveMftRecordReader().ReadAsync(source, 42, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(LiveRecordReadStatus.ObservedUnchanged, result.Status);
        Assert.NotNull(result.Record); Assert.Equal(1, result.Attempts); Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task ReadAsync_DeletedBetweenReads_ReturnsMissingNotZeroSize()
    {
        var result = await new LiveMftRecordReader().ReadAsync(new SequenceSource(RecordFixture.Record(), null), 42, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(LiveRecordReadStatus.Missing, result.Status); Assert.Null(result.Record);
    }

    [Fact]
    public async Task ReadAsync_RecordIndexReused_ReturnsReplacedForExpectedIdentity()
    {
        byte[] bytes = RecordFixture.Record(); RecordFixture.U16(bytes, 16, 4);
        var result = await new LiveMftRecordReader().ReadAsync(new SequenceSource(bytes), 42, FileReference.Create(42, 3), TestContext.Current.CancellationToken);
        Assert.Equal(LiveRecordReadStatus.Replaced, result.Status); Assert.Null(result.Record);
    }

    [Fact]
    public async Task ReadAsync_ChangesThenSettles_RetriesAndReturnsLatestRecord()
    {
        byte[] first = RecordFixture.Record(); byte[] second = RecordFixture.Record(); RecordFixture.U64(second, 8, 123);
        var result = await new LiveMftRecordReader().ReadAsync(new SequenceSource(first, second, second, second), 42, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(LiveRecordReadStatus.ObservedUnchanged, result.Status);
        Assert.Equal(2, result.Attempts); Assert.Equal(123uL, result.Record!.LogSequenceNumber);
    }

    [Fact]
    public async Task ReadAsync_ContinuouslyChanging_BoundsWorkAndReportsUnresolved()
    {
        byte[] first = RecordFixture.Record(); byte[] second = RecordFixture.Record(); RecordFixture.U64(second, 8, 123);
        var source = new SequenceSource(first, second, first, second, first, second);
        var result = await new LiveMftRecordReader().ReadAsync(source, 42, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(LiveRecordReadStatus.ChangedDuringRead, result.Status);
        Assert.Null(result.Record); Assert.Equal(3, result.Attempts); Assert.Equal(6, source.Calls);
    }

    [Fact]
    public async Task ReadAsync_TornRecordThenValidRecord_Retries()
    {
        byte[] broken = RecordFixture.Record(); broken[510] ^= 1;
        byte[] valid = RecordFixture.Record();
        var result = await new LiveMftRecordReader().ReadAsync(new SequenceSource(broken, valid, valid), 42, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(LiveRecordReadStatus.ObservedUnchanged, result.Status); Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task ReadAsync_PermanentlyInvalidRecord_ReportsError()
    {
        var result = await new LiveMftRecordReader().ReadAsync(new SequenceSource(new byte[1024], new byte[1024], new byte[1024]), 42, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(LiveRecordReadStatus.Invalid, result.Status); Assert.NotNull(result.Error); Assert.Null(result.Record);
    }

    [Fact]
    public async Task ReadAsync_Cancelled_DoesNotCallSource()
    {
        var source = new SequenceSource(RecordFixture.Record());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new LiveMftRecordReader().ReadAsync(source, 42, cancellationToken: cancellation.Token));
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData(0)] [InlineData(11)]
    public void Constructor_InvalidAttemptLimit_Rejects(int limit)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new LiveMftRecordReader(limit));

    [Fact]
    public async Task ReadAsync_MissingOnFirstRead_IsNotAParsedZeroLengthFile()
    {
        var result = await new LiveMftRecordReader().ReadAsync(new SequenceSource([null]), 42,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(LiveRecordReadStatus.Missing, result.Status); Assert.Null(result.Record);
    }

    [Fact]
    public async Task ReadAsync_UnusedRecord_IsDistinctFromMissing()
    {
        byte[] bytes = RecordFixture.Record(flags: 0);
        var result = await new LiveMftRecordReader().ReadAsync(new SequenceSource(bytes, bytes), 42,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(LiveRecordReadStatus.NotInUse, result.Status); Assert.NotNull(result.Record);
    }

    [Fact]
    public async Task ReadAsync_ReusedSourceBuffer_DetectsMutationAgainstOwnedCopy()
    {
        var source = new ReusedBufferSource();
        var result = await new LiveMftRecordReader(maximumAttempts: 1).ReadAsync(source, 42,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(LiveRecordReadStatus.ChangedDuringRead, result.Status); Assert.Null(result.Record);
    }

    [Fact]
    public async Task ReadAsync_OperationalIoFailure_IsNotReportedAsMissing()
    {
        await Assert.ThrowsAsync<IOException>(async () => await new LiveMftRecordReader().ReadAsync(new FailedSource(), 42,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_OversizedSourceRecord_IsRejectedWithoutRetry()
    {
        var source = new SequenceSource(new byte[MftRecordParser.MaximumRecordSize + 1]);
        var result = await new LiveMftRecordReader().ReadAsync(source, 42, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(LiveRecordReadStatus.Invalid, result.Status); Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task ReadAsync_ExpectedIdentityForDifferentIndex_IsCallerError()
    {
        var source = new SequenceSource(RecordFixture.Record());
        await Assert.ThrowsAsync<ArgumentException>(async () => await new LiveMftRecordReader().ReadAsync(source, 42,
            FileReference.Create(43, 3), TestContext.Current.CancellationToken));
        Assert.Equal(0, source.Calls);
    }

    private sealed class ReusedBufferSource : IRawMftRecordSource
    {
        private readonly byte[] buffer = RecordFixture.Record();
        private int calls;
        public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ulong recordIndex, CancellationToken cancellationToken)
        {
            RecordFixture.U64(buffer, 8, (ulong)calls++);
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(buffer.AsMemory());
        }
    }

    private sealed class FailedSource : IRawMftRecordSource
    {
        public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ulong recordIndex, CancellationToken cancellationToken)
            => throw new IOException("Device read failed.");
    }

    private sealed class SequenceSource(params byte[]?[] values) : IRawMftRecordSource
    {
        public int Calls { get; private set; }
        public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ulong recordIndex, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? value = values[Calls++];
            if (value is null) return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(value.AsMemory());
        }
    }
}
