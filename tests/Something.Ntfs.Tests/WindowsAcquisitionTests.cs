using System.Buffers.Binary;
using Something.Ntfs;
using Something.Windows;

namespace Something.Ntfs.Tests;

public sealed class WindowsAcquisitionTests
{
    [Fact]
    public void FileInfo_ValidMetadata_PreservesUnsignedIdentityAnd64BitSize()
    {
        byte[] bytes = new byte[52];
        RecordFixture.U32(bytes, 28, uint.MaxValue);
        RecordFixture.U32(bytes, 32, 1);
        RecordFixture.U32(bytes, 36, 42);
        RecordFixture.U32(bytes, 40, 2);
        RecordFixture.U32(bytes, 44, 0xFFFF0000);
        RecordFixture.U32(bytes, 48, 42);
        RecordFixture.U64(bytes, 20, 1234567);
        var info = WindowsFileInfo.Decode(bytes);
        Assert.Equal(FileReference.Create(42, ushort.MaxValue), info.Reference);
        Assert.Equal(4294967338L, info.LogicalBytes);
        Assert.Equal(uint.MaxValue, info.VolumeSerialNumber);
        Assert.Equal(2u, info.LinkCount);
        Assert.Equal(1234567uL, info.LastWriteTime);
        Assert.False(info.IsDirectory);
    }

    [Fact]
    public void FileInfo_DirectoryAndZeroSize_AreExplicit()
    {
        byte[] bytes = new byte[52];
        RecordFixture.U32(bytes, 0, 0x10);
        var info = WindowsFileInfo.Decode(bytes);
        Assert.True(info.IsDirectory);
        Assert.Equal(0, info.LogicalBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    [InlineData(53)]
    public void FileInfo_InvalidResponseLength_Rejects(int length)
    {
        Assert.Throws<InvalidDataException>(() => WindowsFileInfo.Decode(new byte[length]));
    }

    [Fact]
    public void FileInfo_UnsignedSizeOverflow_Rejects()
    {
        byte[] bytes = new byte[52];
        RecordFixture.U32(bytes, 32, 0x80000000);
        Assert.Throws<InvalidDataException>(() => WindowsFileInfo.Decode(bytes));
    }

    [Fact]
    public void FileMetadata_OwnTemporaryFile_ReadsLogicalSizeWithoutElevation()
    {
        if (!OperatingSystem.IsWindows())
            return;
        // This integration fixture is the only file created here; no live volume is opened.
        string path = Path.Combine(
            Path.GetTempPath(),
            $"something-metadata-{Guid.NewGuid():N}.tmp"
        );
        try
        {
            File.WriteAllBytes(path, new byte[12345]);
            using var file = WindowsFileMetadata.Open(Path.GetPathRoot(path)![..2], path);
            var first = file.Observe(TestContext.Current.CancellationToken);
            var second = file.Observe(TestContext.Current.CancellationToken);
            Assert.Equal(12345, first.LogicalBytes);
            Assert.False(first.IsDirectory);
            Assert.Equal(first, second);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Volume_RecordCountBeyondReferenceRange_Rejects()
    {
        byte[] bytes = VolumeResponse();
        RecordFixture.I64(bytes, 16, 1L << 50);
        RecordFixture.I64(bytes, 56, (1L << 58) + 1024);
        Assert.Throws<InvalidDataException>(() => NtfsVolumeInfo.Decode(bytes));
    }

    [Theory]
    [InlineData(512, 512, 512)]
    [InlineData(4096, 2097152, 65536)]
    public void Volume_SupportedBoundaryGeometry_Accepts(int sector, int cluster, int record)
    {
        byte[] bytes = VolumeResponse();
        RecordFixture.U32(bytes, 40, (uint)sector);
        RecordFixture.U32(bytes, 44, (uint)cluster);
        RecordFixture.U32(bytes, 48, (uint)record);
        var info = NtfsVolumeInfo.Decode(bytes);
        Assert.Equal(record, info.BytesPerRecord);
    }

    [Theory]
    [InlineData("C:", @"\\.\C:")]
    [InlineData("z:", @"\\.\Z:")]
    public void DevicePath_LocalDrive_NormalizesLetter(string input, string expected)
    {
        Assert.Equal(expected, WindowsMftRecordSource.DevicePath(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("C")]
    [InlineData("C:\\")]
    [InlineData("1:")]
    [InlineData("Ｃ:")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\server\share")]
    [InlineData("C:\\file")]
    public void DevicePath_NonDriveInput_RejectsDeviceAndFilePaths(string input)
    {
        Assert.Throws<ArgumentException>(() => WindowsMftRecordSource.DevicePath(input));
    }

    [Fact]
    public void Volume_ValidGeometry_ReportsObservedSlots()
    {
        var info = NtfsVolumeInfo.Decode(VolumeResponse());
        Assert.Equal(1024, info.BytesPerRecord);
        Assert.Equal(4096, info.BytesPerCluster);
        Assert.Equal(64uL, info.ObservedRecordCount);
        Assert.Equal(ulong.MaxValue, info.SerialNumber);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(56)]
    [InlineData(95)]
    public void Volume_PartialResponse_Rejects(int size)
    {
        Assert.Throws<InvalidDataException>(() => NtfsVolumeInfo.Decode(VolumeResponse()[..size]));
    }

    [Theory]
    [InlineData(40, 0)]
    [InlineData(40, 8192)]
    [InlineData(40, 513)]
    [InlineData(44, 256)]
    [InlineData(44, 4097)]
    [InlineData(44, 4194304)]
    [InlineData(48, 0)]
    [InlineData(48, 1000)]
    [InlineData(48, 131072)]
    public void Volume_UnsupportedGeometry_Rejects(int offset, uint value)
    {
        byte[] bytes = VolumeResponse();
        RecordFixture.U32(bytes, offset, value);
        Assert.Throws<InvalidDataException>(() => NtfsVolumeInfo.Decode(bytes));
    }

    [Theory]
    [InlineData(16, 0)]
    [InlineData(16, -1)]
    [InlineData(16, long.MaxValue)]
    [InlineData(24, -1)]
    [InlineData(24, 10001)]
    [InlineData(56, 0)]
    [InlineData(56, -1)]
    [InlineData(56, 65537)]
    [InlineData(56, 40961024)]
    public void Volume_InconsistentOrOverflowingLengths_Rejects(int offset, long value)
    {
        byte[] bytes = VolumeResponse();
        RecordFixture.I64(bytes, offset, value);
        Assert.Throws<InvalidDataException>(() => NtfsVolumeInfo.Decode(bytes));
    }

    [Fact]
    public void Record_ExactIndex_ReturnsOwnedBytesWithoutPadding()
    {
        byte[] record = RestoredRecord();
        byte[] response = RecordResponse(record, 42, padding: 4);
        var memory = WindowsMftRecordSource.DecodeRecord(response, 42, 1024);
        Assert.Equal(record, memory!.Value.ToArray());
        response[12] ^= 0xFF;
        Assert.Equal(record, memory.Value.ToArray());
    }

    [Fact]
    public void Record_PreviousInUseIndex_ReportsMissing()
    {
        var result = WindowsMftRecordSource.DecodeRecord(
            RecordResponse(RestoredRecord(), 41),
            42,
            1024
        );
        Assert.Null(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(1035)]
    public void Record_PartialResponse_Rejects(int size)
    {
        byte[] bytes = RecordResponse(RestoredRecord(), 42);
        Assert.Throws<InvalidDataException>(() =>
            WindowsMftRecordSource.DecodeRecord(bytes[..size], 42, 1024)
        );
    }

    [Fact]
    public void Record_ReturnedLaterIndex_Rejects()
    {
        Assert.Throws<InvalidDataException>(() =>
            WindowsMftRecordSource.DecodeRecord(RecordResponse(RestoredRecord(), 43), 42, 1024)
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(512)]
    [InlineData(1025)]
    [InlineData(uint.MaxValue)]
    public void Record_DeclaredLengthDoesNotMatchGeometry_Rejects(uint length)
    {
        byte[] bytes = RecordResponse(RestoredRecord(), 42);
        RecordFixture.U32(bytes, 8, length);
        Assert.Throws<InvalidDataException>(() =>
            WindowsMftRecordSource.DecodeRecord(bytes, 42, 1024)
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(511)]
    [InlineData(1000)]
    [InlineData(65537)]
    public void Record_UnsupportedExpectedSize_RejectsBeforeAllocation(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowsMftRecordSource.DecodeRecord([], 42, size)
        );
    }

    [Fact]
    public async Task Source_RestoredRecord_ComposesWithOptimisticReader()
    {
        var io = new FakeIo();
        using var source = new WindowsMftRecordSource(io);
        var result = await new LiveMftRecordReader().ReadAsync(
            source,
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Equal(LiveRecordReadStatus.ObservedUnchanged, result.Status);
        Assert.Equal(FileReference.Create(42, 3), result.Record!.Reference);
        Assert.Equal(2, io.RecordCalls);
    }

    [Fact]
    public async Task Source_RestoredRecord_FileAssemblyRevalidatesInDeclaredEncoding()
    {
        var io = new FakeIo();
        using var source = new WindowsMftRecordSource(io);
        var result = await new LiveMftFileReader(source.Volume.BytesPerCluster).ReadAsync(
            source,
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        Assert.Equal(5000, Assert.Single(result.File!.DataStreams).LogicalBytes);
        Assert.Equal(4, io.RecordCalls);
    }

    [Fact]
    public async Task Source_DecodedCachedValue_DeclaresWindowsContractAndPreservesCurrentBytes()
    {
        byte[] record = RecordFixture.Record([
            RecordFixture.Resident(0x80, 0, Enumerable.Repeat((byte)0x11, 600).ToArray()),
        ]);
        for (int n = 1; n <= 2; n++)
            record.AsSpan(48 + n * 2, 2).CopyTo(record.AsSpan(n * 512 - 2, 2));
        record[510] = 0x22;
        var io = new FakeIo { RecordBytes = record };
        using var source = new WindowsMftRecordSource(io);

        var result = await new LiveMftFileReader(4096).ReadAsync(
            source,
            42,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(MftRecordEncoding.WindowsDecoded, source.Encoding);
        Assert.Equal(LiveFileReadStatus.ObservedUnchanged, result.Status);
        var attribute = Assert.IsType<ResidentAttribute>(Assert.Single(result.File!.Attributes));
        Assert.Equal(0x22, attribute.Value[430]);
        Assert.Equal(4, io.RecordCalls);
    }

    [Fact]
    public async Task Source_CancelledBeforeRead_DoesNotAcquireRecord()
    {
        var io = new FakeIo();
        using var source = new WindowsMftRecordSource(io);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            source.ReadAsync(42, cancellation.Token).AsTask()
        );
        Assert.Equal(0, io.RecordCalls);
    }

    [Fact]
    public async Task Source_CancelledDuringNativeCall_DiscardsResult()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var io = new FakeIo { OnRead = cancellation.Cancel };
        using var source = new WindowsMftRecordSource(io);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            source.ReadAsync(42, cancellation.Token).AsTask()
        );
        Assert.Equal(1, io.RecordCalls);
    }

    [Fact]
    public async Task Source_OperationalFailure_DoesNotConvertToMissing()
    {
        var failure = new IOException("Synthetic device failure");
        var io = new FakeIo { ReadError = failure };
        using var source = new WindowsMftRecordSource(io);
        var error = await Assert.ThrowsAsync<IOException>(() =>
            source.ReadAsync(42, TestContext.Current.CancellationToken).AsTask()
        );
        Assert.Same(failure, error);
    }

    [Fact]
    public void Source_InvalidVolume_ClosesAcquisitionHandle()
    {
        var io = new FakeIo { VolumeBytes = new byte[96] };
        Assert.Throws<InvalidDataException>(() => new WindowsMftRecordSource(io));
        Assert.Equal(1, io.Disposals);
    }

    [Fact]
    public async Task Source_Disposed_RejectsReadsAndDisposesOnce()
    {
        var io = new FakeIo();
        var source = new WindowsMftRecordSource(io);
        source.Dispose();
        source.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            source.ReadAsync(42, TestContext.Current.CancellationToken).AsTask()
        );
        Assert.Equal(1, io.Disposals);
        Assert.Equal(0, io.RecordCalls);
    }

    [Fact]
    public async Task Source_InvalidIndex_DoesNotCallNativeBackend()
    {
        var io = new FakeIo();
        using var source = new WindowsMftRecordSource(io);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            source
                .ReadAsync(
                    FileReference.MaximumRecordIndex + 1,
                    TestContext.Current.CancellationToken
                )
                .AsTask()
        );
        Assert.Equal(0, io.RecordCalls);
    }

    [Fact]
    public void Parser_RestoredEncoding_DoesNotMutateCallerOrRestoreTwice()
    {
        byte[] bytes = RestoredRecord();
        byte[] original = bytes.ToArray();
        var parser = new MftRecordParser();
        var record = parser.Parse(bytes, 42, MftRecordEncoding.FixupsRestored);
        Assert.Equal(FileReference.Create(42, 3), record.Reference);
        Assert.Equal(original, bytes);
        Assert.Throws<NtfsParseException>(() => parser.Parse(bytes, 42));
        Assert.Throws<NtfsParseException>(() =>
            parser.Parse(RecordFixture.Record(), 42, MftRecordEncoding.FixupsRestored)
        );
    }

    [Fact]
    public void Parser_RestoredTailDoesNotMatchSavedValue_Rejects()
    {
        byte[] bytes = RestoredRecord();
        bytes[510] ^= 1;
        Assert.False(
            new MftRecordParser().TryParse(
                bytes,
                42,
                out var record,
                out var error,
                MftRecordEncoding.FixupsRestored
            )
        );
        Assert.Null(record);
        Assert.NotNull(error);
    }

    [Fact]
    public void Parser_UnknownEncoding_IsCallerError()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MftRecordParser().Parse(RecordFixture.Record(), 42, (MftRecordEncoding)999)
        );
    }

    [Fact]
    public void Decoder_DeterministicMutationInput_OnlyReturnsBoundedOwnedRecordsOrValidationFailure()
    {
        var random = new Random(1037);
        byte[] baseline = RecordResponse(RestoredRecord(), 42);
        for (int n = 0; n < 500; n++)
        {
            byte[] bytes = baseline.ToArray();
            int position = random.Next(12);
            bytes[position] ^= (byte)random.Next(1, 256);
            try
            {
                var result = WindowsMftRecordSource.DecodeRecord(bytes, 42, 1024);
                if (result is { } record)
                    Assert.Equal(1024, record.Length);
            }
            catch (InvalidDataException) { }
        }
    }

    private static byte[] VolumeResponse()
    {
        byte[] bytes = new byte[96];
        RecordFixture.U64(bytes, 0, ulong.MaxValue);
        RecordFixture.I64(bytes, 16, 10000);
        RecordFixture.I64(bytes, 24, 5000);
        RecordFixture.U32(bytes, 40, 512);
        RecordFixture.U32(bytes, 44, 4096);
        RecordFixture.U32(bytes, 48, 1024);
        RecordFixture.I64(bytes, 56, 64 * 1024);
        return bytes;
    }

    private static byte[] RestoredRecord()
    {
        byte[] bytes = RecordFixture.Record();
        for (int n = 1; n <= bytes.Length / 512; n++)
            bytes.AsSpan(48 + 2 * n, 2).CopyTo(bytes.AsSpan(n * 512 - 2, 2));
        return bytes;
    }

    private static byte[] RecordResponse(byte[] record, ulong index, int padding = 0)
    {
        byte[] response = new byte[12 + record.Length + padding];
        BinaryPrimitives.WriteUInt64LittleEndian(response, index);
        RecordFixture.U32(response, 8, (uint)record.Length);
        record.CopyTo(response, 12);
        return response;
    }

    private sealed class FakeIo : INtfsVolumeIo
    {
        public byte[] VolumeBytes { get; init; } = VolumeResponse();
        public byte[] RecordBytes { get; init; } = RestoredRecord();
        public int RecordCalls { get; private set; }
        public int Disposals { get; private set; }
        public Action? OnRead { get; init; }
        public IOException? ReadError { get; init; }

        public byte[] ReadVolumeData() => VolumeBytes;

        public byte[] ReadFileRecord(ulong recordIndex, int recordSize)
        {
            RecordCalls++;
            OnRead?.Invoke();
            if (ReadError is { } error)
                throw error;
            return RecordResponse(RecordBytes, recordIndex);
        }

        public void Dispose() => Disposals++;
    }
}
