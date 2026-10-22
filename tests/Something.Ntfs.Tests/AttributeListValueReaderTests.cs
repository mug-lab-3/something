namespace Something.Ntfs.Tests;

public sealed class AttributeListValueReaderTests
{
    private static NonResidentAttribute Descriptor(long logical = 6000) =>
        new(
            NtfsAttributeTypes.AttributeList,
            0,
            "",
            0,
            0,
            1,
            0,
            new(8192, logical, logical, null),
            [new(0, 1, 10), new(1, 1, 20)]
        );

    [Fact]
    public async Task ReadAsync_FragmentedClusters_TrimsPaddingAndOwnsReusedBuffer()
    {
        byte[] shared = new byte[4096];
        var source = new Source(
            (lcn, _) =>
            {
                Array.Fill(shared, (byte)lcn);
                return shared;
            }
        );
        var result = await new AttributeListValueReader(4096, 100).ReadAsync(
            source,
            Descriptor(),
            TestContext.Current.CancellationToken
        );
        Array.Clear(shared);
        Assert.Equal(6000, result.Length);
        Assert.All(result.ToArray()[..4096], b => Assert.Equal(10, b));
        Assert.All(result.ToArray()[4096..], b => Assert.Equal(20, b));
        Assert.Equal([(10L, 1), (20L, 1)], source.Calls);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    [InlineData(2097152)]
    public async Task ReadAsync_MaximumValue_BoundsChunksIncludingLargeClusters(int cluster)
    {
        const int logical = AttributeListValueReader.MaximumValueBytes;
        long count = (logical + (long)cluster - 1) / cluster;
        var attribute = Descriptor(logical) with
        {
            HighestVcn = count - 1,
            Sizes = new(count * cluster, logical, logical, null),
            Runs = [new(0, count, 1)],
        };
        var source = new Source((_, n) => new byte[n * cluster]);
        var result = await new AttributeListValueReader(cluster, 10000).ReadAsync(
            source,
            attribute,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(logical, result.Length);
        Assert.All(source.Calls, c => Assert.InRange(c.Count, 1, Math.Max(1, 65536 / cluster)));
        Assert.Equal(count, source.Calls.Sum(c => (long)c.Count));
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
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    public async Task ReadAsync_InvalidPlan_RejectsBeforeAnyIo(int scenario)
    {
        var good = Descriptor();
        var bad = scenario switch
        {
            0 => good with { Type = NtfsAttributeTypes.Data },
            1 => good with { Name = "named" },
            2 => good with { Flags = NtfsAttributeFlags.Sparse },
            3 => good with { CompressionUnit = 1 },
            4 => good with { LowestVcn = 1 },
            5 => good with { Sizes = null },
            6 => good with { Sizes = new(8192, 0, 0, null) },
            7 => good with { Sizes = new(8192, 6000, 5999, null) },
            8 => good with { Sizes = new(0, 6000, 6000, null) },
            9 => good with { Sizes = new(8192, 1048577, 1048577, null) },
            10 => good with { Runs = [new(0, 1, 10), new(2, 1, 20)] },
            11 => good with { Runs = [new(0, 1, 10), new(1, 0, 20)] },
            12 => good with { Runs = [new(0, 1, 10), new(1, 1, null)] },
            13 => good with { Runs = [new(0, 1, 10), new(1, 1, -1)] },
            14 => good with { Runs = [new(0, 1, 10), new(1, 1, 1000)] },
            15 => good with { Runs = [new(0, 1, 10), new(1, 2, 999)] },
            16 => good with { Runs = [new(0, 257, 10)] },
            17 => good with { Runs = [new(0, 1, 10)] },
            18 => good with { HighestVcn = 2 },
            19 => good with { Sizes = new(4096, 6000, 6000, null) },
            20 => good with { Sizes = new(8192, 6000, 6000, 8192) },
            21 => good with { Runs = [new(0, 1, 10), new(1, 1, 10)] },
            _ => good with { Runs = [] },
        };
        var source = new Source((_, n) => new byte[n * 4096]);
        var error = await Assert.ThrowsAsync<AttributeListReadException>(() =>
            new AttributeListValueReader(4096, 1000)
                .ReadAsync(source, bad, TestContext.Current.CancellationToken)
                .AsTask()
        );
        Assert.Equal(
            scenario <= 5 ? AttributeListReadFailure.Unsupported
                : scenario is 9 or 16 ? AttributeListReadFailure.LimitExceeded
                : AttributeListReadFailure.Invalid,
            error.Failure
        );
        Assert.Empty(source.Calls);
    }

    [Theory]
    [InlineData(4095)]
    [InlineData(4097)]
    public async Task ReadAsync_InexactClusterRead_ThrowsIoError(int length)
    {
        var source = new Source((_, _) => new byte[length]);
        await Assert.ThrowsAsync<IOException>(() =>
            new AttributeListValueReader(4096, 100)
                .ReadAsync(source, Descriptor(), TestContext.Current.CancellationToken)
                .AsTask()
        );
        Assert.Single(source.Calls);
    }

    [Fact]
    public async Task ReadAsync_BackendFailure_PropagatesWithoutFurtherReads()
    {
        var error = new IOException("synthetic read failure");
        var source = new Source((_, _) => throw error);
        Assert.Same(
            error,
            await Assert.ThrowsAsync<IOException>(() =>
                new AttributeListValueReader(4096, 100)
                    .ReadAsync(source, Descriptor(), TestContext.Current.CancellationToken)
                    .AsTask()
            )
        );
        Assert.Single(source.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_CancellationBeforeOrDuringRead_Stops(bool during)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var source = new Source(
            (_, _) =>
            {
                cancellation.Cancel();
                return new byte[4096];
            }
        );
        if (!during)
            cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AttributeListValueReader(4096, 100)
                .ReadAsync(source, Descriptor(), cancellation.Token)
                .AsTask()
        );
        Assert.Equal(during ? 1 : 0, source.Calls.Count);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(513, 100)]
    [InlineData(4194304, 100)]
    [InlineData(4096, 0)]
    [InlineData(4096, -1)]
    [InlineData(4096, long.MaxValue)]
    public void Constructor_InvalidGeometry_Rejects(int cluster, long count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AttributeListValueReader(cluster, count)
        );

    private sealed class Source(Func<long, int, byte[]> read) : IVolumeClusterSource
    {
        public List<(long Lcn, int Count)> Calls { get; } = [];

        public ValueTask<ReadOnlyMemory<byte>> ReadClustersAsync(
            long startLcn,
            int clusterCount,
            CancellationToken cancellationToken
        )
        {
            Calls.Add((startLcn, clusterCount));
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(read(startLcn, clusterCount));
        }
    }
}
