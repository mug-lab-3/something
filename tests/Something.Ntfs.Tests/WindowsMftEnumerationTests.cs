using Something.Ntfs;
using Something.Windows;

namespace Something.Ntfs.Tests;

public sealed class WindowsMftEnumerationTests
{
    [Fact]
    public async Task EnumerateAsync_Gaps_UsesReturnedIndexAndOwnsRecords()
    {
        var io = new Io
        {
            Response = index =>
                Response(
                    index >= 7 ? 7uL
                    : index >= 3 ? 3uL
                    : 0uL
                ),
        };
        using var source = new WindowsMftRecordSource(io);
        List<MftRecordCandidate> entries = [];
        await foreach (
            var entry in source.EnumerateAsync(new(0, 64), TestContext.Current.CancellationToken)
        )
            entries.Add(entry);
        Assert.Equal([7uL, 3uL, 0uL], entries.Select(e => e.RecordIndex));
        Assert.Equal([63uL, 6uL, 2uL], io.Requests);
        foreach (var entry in entries)
            Assert.Equal(
                entry.RecordIndex,
                new MftRecordParser()
                    .Parse(entry.Bytes.Span, entry.RecordIndex, source.Encoding)
                    .Reference.RecordIndex
            );
        Assert.Equal(3, source.Metrics.RecordRequests);
        Assert.Equal(0, source.Metrics.RawReads);
        Assert.True(source.Metrics.AcquisitionTime >= TimeSpan.Zero);
    }

    [Fact]
    public async Task EnumerateAsync_ReturnedIndexBelowRange_StopsWithoutYieldingIt()
    {
        var io = new Io { Response = index => Response(index >= 7 ? 7uL : 3uL) };
        using var source = new WindowsMftRecordSource(io);
        var entries = await Collect(source, new(4, 4));
        Assert.Equal(7uL, Assert.Single(entries).RecordIndex);
        Assert.Equal([7uL, 6uL], io.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EnumerateAsync_MalformedNativeResponse_ThrowsInsteadOfSkipping(int scenario)
    {
        var io = new Io
        {
            Response = index =>
            {
                byte[] bytes = Response(scenario == 0 ? index + 1 : index);
                if (scenario == 1)
                    RecordFixture.U32(bytes, 8, 1023);
                return scenario == 2 ? bytes[..11] : bytes;
            },
        };
        using var source = new WindowsMftRecordSource(io);
        await Assert.ThrowsAsync<InvalidDataException>(() => Collect(source, new(0, 64)));
        Assert.Single(io.Requests);
    }

    [Fact]
    public async Task EnumerateAsync_OperationalFailure_ThrowsAndCountsAttempt()
    {
        var error = new IOException("synthetic failure");
        var io = new Io { Response = _ => throw error };
        using var source = new WindowsMftRecordSource(io);
        Assert.Same(
            error,
            await Assert.ThrowsAsync<IOException>(() => Collect(source, new(0, 64)))
        );
        Assert.Equal(1, source.Metrics.RecordRequests);
    }

    [Fact]
    public async Task EnumerateAsync_DisposedSource_DoesNotCallBackend()
    {
        var io = new Io();
        var source = new WindowsMftRecordSource(io);
        source.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Collect(source, new(0, 64)));
        Assert.Empty(io.Requests);
    }

    [Fact]
    public async Task EnumerateAsync_CancelDuringNativeRead_DiscardsCandidate()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var io = new Io
        {
            Response = index =>
            {
                cancellation.Cancel();
                return Response(index);
            },
        };
        using var source = new WindowsMftRecordSource(io);
        await using var iterator = source
            .EnumerateAsync(new(0, 64), cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await iterator.MoveNextAsync()
        );
        Assert.Single(io.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnumerateAsync_EmptyOrOutOfVolumeRange_RejectsBeforeIo(bool empty)
    {
        var io = new Io();
        using var source = new WindowsMftRecordSource(io);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Collect(source, empty ? default : new(0, 65))
        );
        Assert.Empty(io.Requests);
    }

    private static async Task<List<MftRecordCandidate>> Collect(
        WindowsMftRecordSource source,
        MftScanRange range
    )
    {
        List<MftRecordCandidate> result = [];
        await foreach (
            var entry in source.EnumerateAsync(range, TestContext.Current.CancellationToken)
        )
            result.Add(entry);
        return result;
    }

    private static byte[] Response(ulong index)
    {
        byte[] record = RecordFixture.Record(index: (uint)index);
        for (int n = 1; n <= record.Length / 512; n++)
            record.AsSpan(48 + 2 * n, 2).CopyTo(record.AsSpan(n * 512 - 2, 2));
        byte[] bytes = new byte[12 + record.Length];
        RecordFixture.U64(bytes, 0, index);
        RecordFixture.U32(bytes, 8, (uint)record.Length);
        record.CopyTo(bytes, 12);
        return bytes;
    }

    private sealed class Io : INtfsVolumeIo
    {
        public Func<ulong, byte[]> Response { get; init; } =
            index => WindowsMftEnumerationTests.Response(index);
        public List<ulong> Requests { get; } = [];

        public byte[] ReadVolumeData()
        {
            byte[] bytes = new byte[96];
            RecordFixture.I64(bytes, 16, 100);
            RecordFixture.I64(bytes, 24, 50);
            RecordFixture.U32(bytes, 40, 512);
            RecordFixture.U32(bytes, 44, 4096);
            RecordFixture.U32(bytes, 48, 1024);
            RecordFixture.I64(bytes, 56, 64 * 1024);
            return bytes;
        }

        public byte[] ReadFileRecord(ulong recordIndex, int recordSize)
        {
            Requests.Add(recordIndex);
            return Response(recordIndex);
        }

        public void Dispose() { }
    }
}
