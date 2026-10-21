using Something.Windows;

namespace Something.Ntfs.Tests;

public sealed class VolumeClusterAcquisitionTests
{
    [Fact]
    public async Task ReadClustersAsync_ValidRange_ConvertsToExactByteRead()
    {
        var io = new Io();
        using var source = new WindowsMftRecordSource(io);
        var result = await source.ReadClustersAsync(99, 1, TestContext.Current.CancellationToken);
        Assert.Equal(4096, result.Length);
        Assert.Equal((99L * 4096, 4096, 512), Assert.Single(io.Reads));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(100, 1)]
    [InlineData(99, 2)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 17)]
    [InlineData(long.MaxValue, int.MaxValue)]
    public async Task ReadClustersAsync_InvalidRange_DoesNotCallBackend(long lcn, int count)
    {
        var io = new Io();
        using var source = new WindowsMftRecordSource(io);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            source.ReadClustersAsync(lcn, count, TestContext.Current.CancellationToken).AsTask()
        );
        Assert.Empty(io.Reads);
    }

    [Theory]
    [InlineData(4095)]
    [InlineData(4097)]
    public async Task ReadClustersAsync_InexactResponse_Throws(int length)
    {
        using var source = new WindowsMftRecordSource(new Io { Read = _ => new byte[length] });
        await Assert.ThrowsAsync<IOException>(() =>
            source.ReadClustersAsync(0, 1, TestContext.Current.CancellationToken).AsTask()
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadClustersAsync_CancellationBeforeOrDuringRead_Stops(bool during)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var io = new Io
        {
            Read = n =>
            {
                cancellation.Cancel();
                return new byte[n];
            },
        };
        using var source = new WindowsMftRecordSource(io);
        if (!during)
            cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            source.ReadClustersAsync(0, 1, cancellation.Token).AsTask()
        );
        Assert.Equal(during ? 1 : 0, io.Reads.Count);
    }

    [Fact]
    public async Task ReadClustersAsync_DisposedSource_DoesNotCallBackend()
    {
        var io = new Io();
        var source = new WindowsMftRecordSource(io);
        source.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            source.ReadClustersAsync(0, 1, TestContext.Current.CancellationToken).AsTask()
        );
        Assert.Empty(io.Reads);
    }

    [Fact]
    public async Task ReadAttributeListAsync_DelegatesValidatedAcquisitionAndTrimsPadding()
    {
        var io = new Io { Read = n => Enumerable.Repeat((byte)123, n).ToArray() };
        using var source = new WindowsMftRecordSource(io);
        var attribute = new NonResidentAttribute(
            0x20,
            0,
            "",
            0,
            0,
            0,
            0,
            new(4096, 32, 32, null),
            [new(0, 1, 99)]
        );
        var result = await source.ReadAttributeListAsync(
            attribute,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(32, result.Length);
        Assert.All(result.ToArray(), b => Assert.Equal(123, b));
        Assert.Single(io.Reads);
    }

    [Theory]
    [InlineData(512, 512, 0)]
    [InlineData(512, 4096, 512)]
    [InlineData(4096, 65536, 0)]
    public void StorageAlignment_ValidDescriptor_ReturnsPhysicalSector(
        uint logical,
        uint physical,
        uint offset
    ) =>
        Assert.Equal(
            (int)physical,
            StorageAlignment.Decode(Alignment(logical, physical, offset), (int)logical)
        );

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 27)]
    [InlineData(4, 27)]
    [InlineData(4, 29)]
    [InlineData(16, 4096)]
    [InlineData(20, 256)]
    [InlineData(20, 4095)]
    [InlineData(20, 131072)]
    [InlineData(24, 4096)]
    public void StorageAlignment_InvalidDescriptor_Rejects(int position, uint value)
    {
        byte[] bytes = Alignment(512, 4096, 0);
        RecordFixture.U32(bytes, position, value);
        Assert.Throws<InvalidDataException>(() => StorageAlignment.Decode(bytes, 512));
    }

    [Fact]
    public void StorageAlignment_TruncatedDescriptor_Rejects() =>
        Assert.Throws<InvalidDataException>(() => StorageAlignment.Decode(new byte[27], 512));

    private static byte[] Alignment(uint logical, uint physical, uint offset)
    {
        byte[] bytes = new byte[28];
        RecordFixture.U32(bytes, 0, 28);
        RecordFixture.U32(bytes, 4, 28);
        RecordFixture.U32(bytes, 16, logical);
        RecordFixture.U32(bytes, 20, physical);
        RecordFixture.U32(bytes, 24, offset);
        return bytes;
    }

    private sealed class Io : INtfsVolumeIo
    {
        public Func<int, byte[]> Read { get; init; } = n => new byte[n];
        public List<(long Offset, int Count, int Sector)> Reads { get; } = [];

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

        public byte[] ReadFileRecord(ulong recordIndex, int recordSize) =>
            throw new NotSupportedException();

        public byte[] ReadVolumeBytes(long offset, int count, int logicalSectorSize)
        {
            Reads.Add((offset, count, logicalSectorSize));
            return Read(count);
        }

        public void Dispose() { }
    }
}
