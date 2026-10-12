using Something.Ntfs;

namespace Something.Ntfs.Tests;

public sealed class LiveMftFileReaderTests
{
    private static readonly FileReference BaseReference = FileReference.Create(42, 3);
    private static readonly FileReference ExtensionReference = FileReference.Create(43, 3);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_OutOfVolumeRun_RevalidatesBeforeReportingFailure(bool changes)
    {
        byte[] outside = RecordFixture.Record();
        byte[] inside = RecordFixture.Record([RecordFixture.NonResident(runs: [0x11, 2, 99, 0])]);
        var source = new Source((_, call) => changes && call > 2 ? inside : outside);

        var result = await new LiveMftFileReader(4096, totalClusters: 101).ReadAsync(
            source,
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        if (changes)
        {
            Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
            Assert.Equal(2, result.Attempts);
            Assert.Equal(8192, Assert.Single(result.File!.DataStreams).AllocatedBytes);
        }
        else
        {
            Assert.Equal(LiveFileReadStatus.InvalidAssembly, result.Status);
            Assert.Null(result.File);
            Assert.Contains("volume bounds", result.Error);
            Assert.Equal(4, source.TotalCalls);
        }
    }

    [Theory]
    [InlineData(LiveFileReadStatus.Missing)]
    [InlineData(LiveFileReadStatus.NotInUse)]
    [InlineData(LiveFileReadStatus.Replaced)]
    [InlineData(LiveFileReadStatus.InvalidRecord)]
    public async Task ReadAsync_UnresolvedBase_DoesNotReturnCapacity(LiveFileReadStatus status)
    {
        byte[]? bytes = status switch
        {
            LiveFileReadStatus.Missing => null,
            LiveFileReadStatus.NotInUse => RecordFixture.Record(flags: 0),
            LiveFileReadStatus.InvalidRecord => new byte[1024],
            _ => RecordFixture.Record(),
        };
        if (status == LiveFileReadStatus.Replaced)
            RecordFixture.U16(bytes!, 16, 4);
        var source = new Source((_, _) => bytes);
        var result = await new LiveMftFileReader(4096).ReadAsync(
            source,
            42,
            BaseReference,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(status, result.Status);
        Assert.Equal(42uL, result.UnresolvedRecordIndex);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task ReadAsync_ExtensionUsedAsBase_Rejects()
    {
        byte[] bytes = RecordFixture.Record(baseReference: ExtensionReference.Value);
        var result = await Read(new Source((_, _) => bytes));
        Assert.Equal(LiveFileReadStatus.InvalidAssembly, result.Status);
        Assert.Null(result.File);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_MultipleOrNamedLists_RejectsInvalidDeclarations(bool named)
    {
        byte[][] attrs = named
            ? [RecordFixture.Resident(0x20, 0, [], "invalid")]
            : [RecordFixture.Resident(0x20, 0, []), RecordFixture.Resident(0x20, 1, [])];
        var source = new Source((_, _) => RecordFixture.Record(attrs));
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.InvalidAssembly, result.Status);
        Assert.Equal(2, source.TotalCalls);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task ReadAsync_ListRefersToStaleBase_RejectsBeforeAcquiring()
    {
        byte[] bytes = RecordFixture.Record([
            RecordFixture.Resident(
                0x20,
                0,
                RecordFixture.ListEntry(0x80, 1, FileReference.Create(42, 4))
            ),
            RecordFixture.NonResident(),
        ]);
        var source = new Source((_, _) => bytes);
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.InvalidAssembly, result.Status);
        Assert.Equal(2, source.TotalCalls);
    }

    [Fact]
    public async Task ReadAsync_BaseContinuouslyChangesWithinReads_BoundsAcquisition()
    {
        var source = new Source(
            (_, n) =>
            {
                byte[] bytes = RecordFixture.Record();
                RecordFixture.U64(bytes, 8, (ulong)n);
                return bytes;
            }
        );
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.ChangedDuringRead, result.Status);
        Assert.Equal(18, source.TotalCalls);
        Assert.Equal(42uL, result.UnresolvedRecordIndex);
        Assert.Null(result.File);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_ExtensionChangesWithinAcquisitionOrValidation_RetriesAndSettles(
        bool duringValidation
    )
    {
        var (baseBytes, extension) = Extended();
        var source = new Source(
            (index, n) =>
            {
                if (index == 42)
                    return baseBytes;
                byte[] bytes = extension.ToArray();
                ulong lsn = duringValidation
                    ? n <= 2
                        ? 0uL
                        : n <= 8
                            ? (ulong)n
                            : 100uL
                    : n <= 6
                        ? (ulong)n
                        : 100uL;
                RecordFixture.U64(bytes, 8, lsn);
                return bytes;
            }
        );
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(16384, Assert.Single(result.File!.DataStreams).AllocatedBytes);
    }

    [Fact]
    public async Task ReadAsync_BaseChangesDuringFinalValidation_RetriesAndSettles()
    {
        var source = new Source(
            (_, n) =>
            {
                byte[] bytes = RecordFixture.Record();
                RecordFixture.U64(
                    bytes,
                    8,
                    n <= 2 ? 0uL
                        : n <= 8 ? (ulong)n
                        : 100uL
                );
                return bytes;
            }
        );
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task ReadAsync_AssemblyMismatchDuringChange_RetriesInsteadOfReportingCorruption()
    {
        var (baseBytes, extension) = Extended();
        byte[] changedBase = baseBytes.ToArray();
        RecordFixture.U64(changedBase, 8, 100);
        byte[] staleExtension = extension.ToArray();
        RecordFixture.U64(staleExtension, 32, FileReference.Create(42, 4).Value);
        var result = await Read(
            new Source(
                (index, n) =>
                    index == 42
                        ? n <= 2
                            ? baseBytes
                            : changedBase
                        : n <= 2
                            ? staleExtension
                            : extension
            )
        );
        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task ReadAsync_RecordCountExactlyAtLimit_AllowsCompleteFile()
    {
        var (baseBytes, extension) = Extended();
        var source = new Source((index, _) => index == 42 ? baseBytes : extension);
        var result = await new LiveMftFileReader(4096, maximumRecords: 2).ReadAsync(
            source,
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        Assert.NotNull(result.File);
    }

    [Fact]
    public async Task ReadAsync_SingleRecord_RevalidatesAfterAssembly()
    {
        var source = new Source((_, _) => RecordFixture.Record());
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        Assert.Equal(5000, Assert.Single(result.File!.DataStreams).LogicalBytes);
        Assert.Equal(4, source.TotalCalls);
    }

    [Theory]
    [InlineData(169, 128, false)]
    [InlineData(169, 169, true)]
    [InlineData(169, 256, true)]
    [InlineData(289, null, true)]
    [InlineData(317, null, true)]
    [InlineData(1024, null, true)]
    [InlineData(1025, null, false)]
    [InlineData(1025, 2048, true)]
    public async Task ReadAsync_ManySegments_RespectsDefaultOrSelectedBudgetAndRevalidatesAll(
        int segmentCount,
        int? limit,
        bool resolves
    )
    {
        byte[] list = Enumerable
            .Range(0, segmentCount)
            .SelectMany(i =>
                RecordFixture.ListEntry(0x80, 1, FileReference.Create((ulong)(42 + i), 3), i)
            )
            .ToArray();
        byte[] baseBytes = RecordFixture.Record(
            [
                RecordFixture.Resident(0x20, 0, list),
                RecordFixture.NonResident(
                    highestVcn: 0,
                    runs: [0x11, 1, 100, 0],
                    logical: segmentCount * 4096,
                    allocated: segmentCount * 4096
                ),
            ],
            size: 65536
        );
        var source = new Source(
            (index, _) =>
                index == 42
                    ? baseBytes
                    : RecordFixture.Record(
                        [
                            RecordFixture.NonResident(
                                lowestVcn: (long)index - 42,
                                highestVcn: (long)index - 42,
                                runs: [0x21, 1, (byte)index, (byte)(index >> 8), 0]
                            ),
                        ],
                        baseReference: BaseReference.Value,
                        index: (uint)index
                    )
        );

        var reader = limit is { } selected
            ? new LiveMftFileReader(4096, maximumRecords: selected, totalClusters: 4096)
            : new LiveMftFileReader(4096, totalClusters: 4096);
        var result = await reader.ReadAsync(
            source,
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(
            resolves ? LiveFileReadStatus.ObservedUnchanged : LiveFileReadStatus.LimitExceeded,
            result.Status
        );
        if (resolves)
        {
            var stream = Assert.Single(result.File!.DataStreams);
            Assert.Equal(segmentCount * 4096, stream.AllocatedBytes);
            Assert.Equal(segmentCount, stream.Runs.Length);
            Assert.Equal(segmentCount, source.Calls.Count);
            Assert.All(source.Calls.Values, calls => Assert.Equal(4, calls));
        }
        else
        {
            Assert.Null(result.File);
            Assert.Equal(2, source.TotalCalls);
        }
    }

    [Fact]
    public async Task ReadAsync_ExtendedFile_ResolvesAndRevalidatesAllRecords()
    {
        var (baseBytes, extension) = Extended();
        var source = new Source((index, _) => index == 42 ? baseBytes : extension);
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        var stream = Assert.Single(result.File!.DataStreams);
        Assert.Equal(16384, stream.AllocatedBytes);
        Assert.Equal(5000, stream.LogicalBytes);
        Assert.Equal(2, stream.Runs.Length);
        Assert.Equal(4, source.Calls[42]);
        Assert.Equal(4, source.Calls[43]);
    }

    [Fact]
    public async Task ReadAsync_ExtendedNamesAndNamedStream_PreservesAllValues()
    {
        byte[] list =
        [
            .. RecordFixture.ListEntry(0x30, 0, BaseReference),
            .. RecordFixture.ListEntry(0x80, 1, BaseReference),
            .. RecordFixture.ListEntry(0x30, 7, ExtensionReference),
            .. RecordFixture.ListEntry(0x30, 8, ExtensionReference),
            .. RecordFixture.ListEntry(0x80, 9, ExtensionReference, name: "metadata"),
        ];
        byte[] baseBytes = RecordFixture.Record([
            RecordFixture.Resident(0x30, 0, RecordFixture.FileName("first.txt")),
            RecordFixture.NonResident(),
            RecordFixture.Resident(0x20, 2, list),
        ]);
        byte[] extension = RecordFixture.Record(
            [
                RecordFixture.Resident(0x30, 7, RecordFixture.FileName("second.txt")),
                RecordFixture.Resident(
                    0x30,
                    8,
                    RecordFixture.FileName("SECOND~1.TXT", FileNameNamespace.Dos)
                ),
                RecordFixture.Resident(0x80, 9, [1, 2, 3], "metadata"),
            ],
            baseReference: BaseReference.Value,
            index: 43
        );
        var result = await Read(new Source((index, _) => index == 42 ? baseBytes : extension));
        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        Assert.Equal(3, result.File!.Names.Length);
        Assert.Contains(result.File.Names, n => n.Namespace == FileNameNamespace.Dos);
        Assert.Equal(
            3,
            Assert.Single(result.File.DataStreams, s => s.Name == "metadata").LogicalBytes
        );
    }

    [Fact]
    public async Task ReadAsync_MissingExtension_DoesNotReturnPartialCapacity()
    {
        var (baseBytes, _) = Extended();
        var result = await Read(new Source((index, _) => index == 42 ? baseBytes : null));
        Assert.Equal(LiveFileReadStatus.Missing, result.Status);
        Assert.Equal(43uL, result.UnresolvedRecordIndex);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task ReadAsync_ReusedExtension_ReportsReplaced()
    {
        var (baseBytes, extension) = Extended();
        RecordFixture.U16(extension, 16, 4);
        var result = await Read(new Source((index, _) => index == 42 ? baseBytes : extension));
        Assert.Equal(LiveFileReadStatus.Replaced, result.Status);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task ReadAsync_ExtensionDisappearsAfterAssembly_ReportsMissing()
    {
        var (baseBytes, extension) = Extended();
        var result = await Read(
            new Source(
                (index, n) =>
                    index == 42 ? baseBytes
                    : n <= 2 ? extension
                    : null
            )
        );
        Assert.Equal(LiveFileReadStatus.Missing, result.Status);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task ReadAsync_ExtensionReusedAfterAssembly_ReportsReplaced()
    {
        var (baseBytes, extension) = Extended();
        byte[] reused = extension.ToArray();
        RecordFixture.U16(reused, 16, 4);
        var result = await Read(
            new Source(
                (index, n) =>
                    index == 42 ? baseBytes
                    : n <= 2 ? extension
                    : reused
            )
        );
        Assert.Equal(LiveFileReadStatus.Replaced, result.Status);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task ReadAsync_BaseChangesAfterAssembly_RetriesWholeFile()
    {
        var (baseBytes, extension) = Extended();
        byte[] changed = baseBytes.ToArray();
        RecordFixture.U64(changed, 8, 100);
        var source = new Source(
            (index, n) =>
                index == 42
                    ? n <= 2
                        ? baseBytes
                        : changed
                    : extension
        );
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(16384, Assert.Single(result.File!.DataStreams).AllocatedBytes);
    }

    [Fact]
    public async Task ReadAsync_ExtensionChangesAfterAssembly_RetriesWholeFile()
    {
        var (baseBytes, extension) = Extended();
        byte[] changed = extension.ToArray();
        RecordFixture.U64(changed, 8, 100);
        var result = await Read(
            new Source(
                (index, n) =>
                    index == 42 ? baseBytes
                    : n <= 2 ? extension
                    : changed
            )
        );
        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task ReadAsync_BaseChangesForever_BoundsWholeFileRetries()
    {
        var source = new Source(
            (_, n) =>
            {
                byte[] bytes = RecordFixture.Record();
                RecordFixture.U64(bytes, 8, (ulong)((n - 1) / 2));
                return bytes;
            }
        );
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.ChangedDuringRead, result.Status);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(12, source.TotalCalls);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task ReadAsync_BaseReusedOnRetry_DoesNotSwitchToNewFile()
    {
        byte[] original = RecordFixture.Record();
        byte[] changed = original.ToArray();
        RecordFixture.U64(changed, 8, 100);
        byte[] reused = changed.ToArray();
        RecordFixture.U16(reused, 16, 4);
        var result = await Read(
            new Source(
                (_, n) =>
                    n <= 2 ? original
                    : n <= 4 ? changed
                    : reused
            )
        );
        Assert.Equal(LiveFileReadStatus.Replaced, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task ReadAsync_NonresidentList_ReportsUnsupportedWithoutReadingExtents()
    {
        byte[] bytes = RecordFixture.Record([
            RecordFixture.NonResident(type: 0x20, id: 0, logical: 64),
            RecordFixture.NonResident(id: 1),
        ]);
        var source = new Source((_, _) => bytes);
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.Unsupported, result.Status);
        Assert.Equal(2, source.TotalCalls);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task ReadAsync_TooManyRecords_StopsBeforeExtensionAcquisition()
    {
        var (baseBytes, _) = Extended();
        var source = new Source((_, _) => baseBytes);
        var result = await new LiveMftFileReader(4096, maximumRecords: 1).ReadAsync(
            source,
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Equal(LiveFileReadStatus.LimitExceeded, result.Status);
        Assert.Equal(2, source.TotalCalls);
        Assert.Null(result.File);
        Assert.Equal(
            "File record acquisition limit exceeded: required-records=2; maximum-records=1; extension-records=1.",
            result.Error
        );
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReadAsync_RecordLimit_CountsDistinctExtensionsAndBase(
        bool includesBase,
        bool multipleAttributesPerExtension
    )
    {
        byte[] list =
        [
            .. includesBase ? RecordFixture.ListEntry(0x80, 1, BaseReference) : [],
            .. RecordFixture.ListEntry(0x80, 2, ExtensionReference, 2),
            .. RecordFixture.ListEntry(0x80, 3, FileReference.Create(44, 3), 4),
            .. multipleAttributesPerExtension
                ? RecordFixture.ListEntry(0x30, 4, ExtensionReference)
                : [],
        ];
        byte[] bytes = RecordFixture.Record([
            RecordFixture.Resident(0x20, 0, list),
            RecordFixture.NonResident(),
        ]);
        var source = new Source((_, _) => bytes);

        var result = await new LiveMftFileReader(4096, maximumRecords: 2).ReadAsync(
            source,
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(LiveFileReadStatus.LimitExceeded, result.Status);
        Assert.Null(result.File);
        Assert.Equal(2, source.TotalCalls);
        Assert.Equal([42uL], source.Calls.Keys);
        Assert.Equal(
            "File record acquisition limit exceeded: required-records=3; maximum-records=2; extension-records=2.",
            result.Error
        );
    }

    [Fact]
    public async Task ReadAsync_ConflictingReferences_RejectsBeforeFollowingLinks()
    {
        byte[] list =
        [
            .. RecordFixture.ListEntry(0x80, 1, BaseReference),
            .. RecordFixture.ListEntry(0x80, 7, ExtensionReference),
            .. RecordFixture.ListEntry(0x80, 8, FileReference.Create(43, 4)),
        ];
        byte[] bytes = RecordFixture.Record([
            RecordFixture.Resident(0x20, 0, list),
            RecordFixture.NonResident(),
        ]);
        var source = new Source((_, _) => bytes);
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.InvalidAssembly, result.Status);
        Assert.Equal(2, source.TotalCalls);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task ReadAsync_ExtensionPointsToDifferentBase_RejectsAssembly()
    {
        var (baseBytes, extension) = Extended();
        RecordFixture.U64(extension, 32, FileReference.Create(42, 4).Value);
        var result = await Read(new Source((index, _) => index == 42 ? baseBytes : extension));
        Assert.Equal(LiveFileReadStatus.InvalidAssembly, result.Status);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task ReadAsync_ExtensionContainsAttributeList_ReportsUnsupported()
    {
        var (baseBytes, _) = Extended();
        byte[] extension = RecordFixture.Record(
            [
                RecordFixture.Resident(
                    0x20,
                    0,
                    RecordFixture.ListEntry(0x80, 7, ExtensionReference, 2)
                ),
                RecordFixture.NonResident(id: 7, lowestVcn: 2, highestVcn: 3),
            ],
            baseReference: BaseReference.Value,
            index: 43
        );
        var result = await Read(new Source((index, _) => index == 42 ? baseBytes : extension));
        Assert.Equal(LiveFileReadStatus.Unsupported, result.Status);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task ReadAsync_SharedAcquisitionBuffer_DoesNotLoseEarlierObservation()
    {
        var (baseBytes, extension) = Extended();
        byte[] buffer = new byte[1024];
        var source = new Source(
            (index, _) =>
            {
                (index == 42 ? baseBytes : extension).CopyTo(buffer, 0);
                return buffer;
            }
        );
        var result = await Read(source);
        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        Assert.Equal(16384, Assert.Single(result.File!.DataStreams).AllocatedBytes);
    }

    [Fact]
    public async Task ReadAsync_DeviceFailure_PropagatesRatherThanReturningZeroCapacity()
    {
        var failure = new IOException("Synthetic I/O failure");
        var source = new Source((_, _) => throw failure);
        var error = await Assert.ThrowsAsync<IOException>(() => Read(source).AsTask());
        Assert.Same(failure, error);
    }

    [Fact]
    public async Task ReadAsync_CancelledBeforeRead_DoesNotAcquire()
    {
        var source = new Source((_, _) => RecordFixture.Record());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LiveMftFileReader(4096)
                .ReadAsync(source, 42, cancellationToken: cancellation.Token)
                .AsTask()
        );
        Assert.Equal(0, source.TotalCalls);
    }

    [Fact]
    public async Task ReadAsync_CancelledDuringFinalValidation_DoesNotPublishFile()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var source = new Source(
            (_, n) =>
            {
                if (n == 4)
                    cancellation.Cancel();
                return RecordFixture.Record();
            }
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LiveMftFileReader(4096)
                .ReadAsync(source, 42, cancellationToken: cancellation.Token)
                .AsTask()
        );
    }

    [Theory]
    [InlineData(0, 128)]
    [InlineData(11, 128)]
    [InlineData(3, 0)]
    [InlineData(3, -1)]
    public void Constructor_UnboundedOrInvalidWorkLimits_Rejects(int attempts, int records)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LiveMftFileReader(4096, attempts, records)
        );
    }

    [Fact]
    public async Task ReadAsync_ExpectedReferenceIndexMismatch_RejectsBeforeAcquisition()
    {
        var source = new Source((_, _) => RecordFixture.Record());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new LiveMftFileReader(4096)
                .ReadAsync(source, 42, ExtensionReference, TestContext.Current.CancellationToken)
                .AsTask()
        );
        Assert.Equal(0, source.TotalCalls);
    }

    private static ValueTask<LiveFileReadResult> Read(Source source) =>
        new LiveMftFileReader(4096).ReadAsync(
            source,
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );

    private static (byte[] Base, byte[] Extension) Extended()
    {
        byte[] list =
        [
            .. RecordFixture.ListEntry(0x80, 1, BaseReference),
            .. RecordFixture.ListEntry(0x80, 0, ExtensionReference, 2),
        ];
        return (
            RecordFixture.Record([
                RecordFixture.Resident(0x20, 0, list),
                RecordFixture.NonResident(allocated: 16384),
            ]),
            RecordFixture.Record(
                [RecordFixture.NonResident(id: 7, lowestVcn: 2, highestVcn: 3)],
                baseReference: BaseReference.Value,
                index: 43
            )
        );
    }

    private sealed class Source(Func<ulong, int, byte[]?> acquire) : IRawMftRecordSource
    {
        public Dictionary<ulong, int> Calls { get; } = [];
        public int TotalCalls => Calls.Values.Sum();

        public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
            ulong recordIndex,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = Calls.GetValueOrDefault(recordIndex) + 1;
            Calls[recordIndex] = n;
            byte[]? bytes = acquire(recordIndex, n);
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                bytes is null ? (ReadOnlyMemory<byte>?)null : bytes.AsMemory()
            );
        }
    }
}
