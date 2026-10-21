namespace Something.Ntfs.Tests;

public sealed class NonResidentListAcquisitionTests
{
    private static readonly FileReference Base = FileReference.Create(42, 3);
    private static readonly FileReference Extension = FileReference.Create(43, 3);

    private static byte[] List() =>
        [
            .. RecordFixture.ListEntry(0x80, 1, Base),
            .. RecordFixture.ListEntry(0x80, 0, Extension, 2),
        ];

    private static byte[] BaseRecord(long? length = null) =>
        RecordFixture.Record([
            RecordFixture.NonResident(
                id: 0,
                highestVcn: 0,
                runs: [0x11, 1, 50, 0],
                logical: length ?? List().Length,
                allocated: 4096,
                type: 0x20
            ),
            RecordFixture.NonResident(id: 1, allocated: 16384),
        ]);

    private static byte[] ExtensionRecord() =>
        RecordFixture.Record(
            [RecordFixture.NonResident(id: 7, lowestVcn: 2, highestVcn: 3)],
            baseReference: Base.Value,
            index: 43
        );

    [Fact]
    public async Task ReadAsync_ExternalList_AssemblesCompleteStreamAndRevalidates()
    {
        var source = new Source();
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        var stream = Assert.Single(result.File!.DataStreams);
        Assert.Equal(5000, stream.LogicalBytes);
        Assert.Equal(16384, stream.AllocatedBytes);
        Assert.Equal(2, stream.Runs.Length);
        Assert.Equal(3, source.ListCalls);
        Assert.Equal(8, source.BaseCalls);
        Assert.Equal(4, source.ExtensionCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_ListChangesDuringAcquisitionOrFinalCheck_RetriesAndSettles(
        bool final
    )
    {
        byte[] shared = List();
        var source = new Source
        {
            ReadList = n =>
            {
                List().CopyTo(shared, 0);
                if (n == (final ? 3 : 2))
                    shared[16] ^= 1;
                return shared;
            },
        };
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.NotNull(result.File);
    }

    [Fact]
    public async Task ReadAsync_ListNeverSettles_ReturnsUncertaintyWithBoundedRetries()
    {
        var source = new Source
        {
            ReadList = n =>
            {
                byte[] bytes = List();
                bytes[16] = (byte)n;
                return bytes;
            },
        };
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.ChangedDuringRead, result.Status);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(6, source.ListCalls);
        Assert.Null(result.File);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_DescriptorChangesAfterExternalRead_Retries(bool final)
    {
        var source = new Source
        {
            ReadBase = n =>
            {
                byte[] bytes = BaseRecord();
                if (n >= (final ? 7 : 3))
                    RecordFixture.U64(bytes, 8, 100);
                return bytes;
            },
        };
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        Assert.Equal(2, result.Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_BaseReferenceReusedAfterExternalRead_RejectsNewIdentity(bool final)
    {
        var source = new Source
        {
            ReadBase = n =>
            {
                byte[] bytes = BaseRecord();
                if (n >= (final ? 7 : 3))
                    RecordFixture.U16(bytes, 16, 4);
                return bytes;
            },
        };
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.Replaced, result.Status);
        Assert.Null(result.File);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_BaseDisappearsAfterExternalRead_ReturnsMissing(bool final)
    {
        var source = new Source { ReadBase = n => n >= (final ? 7 : 3) ? null : BaseRecord() };
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.Missing, result.Status);
        Assert.Null(result.File);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ReadAsync_IncorrectLengthOrMalformedList_ReturnsNoCapacity(int scenario)
    {
        var source = new Source
        {
            ReadList = _ => scenario == 0 ? new byte[1] : new byte[List().Length],
        };
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.InvalidAssembly, result.Status);
        Assert.Null(result.File);
        Assert.Equal(0, source.ExtensionCalls);
    }

    [Fact]
    public async Task ReadAsync_OverLimitValue_RejectsBeforeExternalRead()
    {
        var source = new Source
        {
            ReadBase = _ => BaseRecord(AttributeListValueReader.MaximumValueBytes + 1L),
        };
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.LimitExceeded, result.Status);
        Assert.Equal(0, source.ListCalls);
        Assert.Null(result.File);
    }

    [Theory]
    [InlineData(AttributeListReadFailure.Invalid, LiveFileReadStatus.InvalidAssembly, false)]
    [InlineData(AttributeListReadFailure.Unsupported, LiveFileReadStatus.Unsupported, false)]
    [InlineData(AttributeListReadFailure.LimitExceeded, LiveFileReadStatus.LimitExceeded, false)]
    [InlineData(AttributeListReadFailure.Invalid, LiveFileReadStatus.InvalidAssembly, true)]
    public async Task ReadAsync_TypedFailure_PreservesClassification(
        AttributeListReadFailure failure,
        LiveFileReadStatus status,
        bool final
    )
    {
        var source = new Source
        {
            ReadList = n =>
                n == (final ? 3 : 1)
                    ? throw new AttributeListReadException(failure, "synthetic failure")
                    : List(),
        };
        var result = await Read(source);
        Assert.Equal(status, result.Status);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task ReadAsync_IoFailure_Propagates()
    {
        var error = new IOException("synthetic failure");
        var source = new Source { ReadList = _ => throw error };
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => Read(source)));
    }

    [Fact]
    public async Task ReadAsync_CancelledByListSource_DoesNotUseReturnedBytes()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var source = new Source
        {
            ReadList = _ =>
            {
                cancellation.Cancel();
                return List();
            },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LiveMftFileReader(4096)
                .ReadAsync(source, 42, cancellationToken: cancellation.Token)
                .AsTask()
        );
        Assert.Equal(1, source.ListCalls);
        Assert.Equal(0, source.ExtensionCalls);
    }

    private static Task<LiveFileReadResult> Read(Source source) =>
        new LiveMftFileReader(4096)
            .ReadAsync(source, 42, cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();

    private sealed class Source : IRawMftRecordSource, IAttributeListValueSource
    {
        public Func<int, byte[]?> ReadBase { get; init; } = _ => BaseRecord();
        public Func<int, byte[]> ReadList { get; init; } = _ => List();
        public int BaseCalls { get; private set; }
        public int ExtensionCalls { get; private set; }
        public int ListCalls { get; private set; }

        public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
            ulong index,
            CancellationToken cancellationToken
        )
        {
            byte[]? bytes;
            if (index == 42)
                bytes = ReadBase(++BaseCalls);
            else
            {
                Assert.Equal(43uL, index);
                ExtensionCalls++;
                bytes = ExtensionRecord();
            }
            return ValueTask.FromResult(
                bytes is null ? (ReadOnlyMemory<byte>?)null : bytes.AsMemory()
            );
        }

        public ValueTask<ReadOnlyMemory<byte>> ReadAttributeListAsync(
            NonResidentAttribute attribute,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<ReadOnlyMemory<byte>>(ReadList(++ListCalls));
    }
}
