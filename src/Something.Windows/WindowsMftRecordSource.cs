using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Something.Ntfs;

namespace Something.Windows;

// Typed seam: there is no generic control-code entry point available to acquisition clients.
internal interface INtfsVolumeIo : IDisposable
{
    byte[] ReadVolumeData();
    byte[] ReadFileRecord(ulong recordIndex, int recordSize);
    byte[] ReadVolumeBytes(long offset, int count, int logicalSectorSize) =>
        throw new NotSupportedException("Raw volume reads are unavailable.");
}

/// <summary>
/// Read-only, synchronous Windows metadata acquisition exposed as a ValueTask source.
/// Run on a worker thread. Cancellation is checked around each native call, not during it.
/// No snapshot guarantee; an earlier in-use record is reported as Missing for an exact-index request.
/// </summary>
public sealed class WindowsMftRecordSource
    : IMftScanSource,
        IAttributeListValueSource,
        IVolumeClusterSource,
        IDisposable
{
    private readonly INtfsVolumeIo io;
    private readonly object gate = new();
    private bool disposed;
    private long recordRequests;
    private long rawReads;
    private long acquisitionTicks;

    public VolumeReadMetrics Metrics =>
        new(
            Interlocked.Read(ref recordRequests),
            Interlocked.Read(ref rawReads),
            TimeSpan.FromSeconds(
                (double)Interlocked.Read(ref acquisitionTicks) / Stopwatch.Frequency
            )
        );

    public NtfsVolumeInfo Volume { get; }
    public MftRecordEncoding Encoding => MftRecordEncoding.WindowsDecoded;

    internal WindowsMftRecordSource(INtfsVolumeIo io)
    {
        this.io = io;
        try
        {
            Volume = NtfsVolumeInfo.Decode(io.ReadVolumeData());
        }
        catch
        {
            io.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    public static WindowsMftRecordSource Open(string drive) => new(NativeNtfsVolumeIo.Open(drive));

    internal static string DevicePath(string drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        if (drive.Length != 2 || drive[1] != ':' || !char.IsAsciiLetter(drive[0]))
            throw new ArgumentException("Specify a local drive letter such as C:.", nameof(drive));
        return @"\\.\" + char.ToUpperInvariant(drive[0]) + ":";
    }

    public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
        ulong recordIndex,
        CancellationToken cancellationToken
    )
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            recordIndex,
            FileReference.MaximumRecordIndex
        );
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            byte[] output = ReadNativeRecord(recordIndex);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(DecodeRecord(output, recordIndex, Volume.BytesPerRecord));
        }
    }

    public async IAsyncEnumerable<MftRecordCandidate> EnumerateAsync(
        MftScanRange range,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (range.RecordCount == 0 || range.EndExclusive > Volume.ObservedRecordCount)
            throw new ArgumentOutOfRangeException(nameof(range));
        ulong cursor = range.EndExclusive - 1;
        while (true)
        {
            var candidate = await ReadCandidateAsync(cursor, cancellationToken)
                .ConfigureAwait(false);
            if (candidate.RecordIndex < range.StartIndex)
                yield break;
            yield return candidate;
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.RecordIndex == range.StartIndex)
                yield break;
            // FSCTL returns the nearest in-use record at or below the cursor: skip the observed gap.
            cursor = candidate.RecordIndex - 1;
        }
    }

    private ValueTask<MftRecordCandidate> ReadCandidateAsync(ulong cursor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            byte[] output = ReadNativeRecord(cursor);
            token.ThrowIfCancellationRequested();
            // Validate against the requested cursor before using the returned identity.
            ulong index = ValidateRecordResponse(output, cursor, Volume.BytesPerRecord);
            return ValueTask.FromResult(
                new MftRecordCandidate(index, output.AsSpan(12, Volume.BytesPerRecord).ToArray())
            );
        }
    }

    private byte[] ReadNativeRecord(ulong index)
    {
        Interlocked.Increment(ref recordRequests);
        long start = Stopwatch.GetTimestamp();
        try
        {
            return io.ReadFileRecord(index, Volume.BytesPerRecord);
        }
        finally
        {
            Interlocked.Add(ref acquisitionTicks, Stopwatch.GetTimestamp() - start);
        }
    }

    internal static ReadOnlyMemory<byte>? DecodeRecord(
        ReadOnlySpan<byte> output,
        ulong requestedIndex,
        int recordSize
    )
    {
        ulong returned = ValidateRecordResponse(output, requestedIndex, recordSize);
        // Do not substitute the previous active file when the requested slot disappeared.
        if (returned != requestedIndex)
            return null;
        return output.Slice(12, recordSize).ToArray();
    }

    private static ulong ValidateRecordResponse(
        ReadOnlySpan<byte> output,
        ulong requestedIndex,
        int recordSize
    )
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            requestedIndex,
            FileReference.MaximumRecordIndex
        );
        if (
            recordSize is < 512 or > MftRecordParser.MaximumRecordSize
            || !System.Numerics.BitOperations.IsPow2((uint)recordSize)
        )
            throw new ArgumentOutOfRangeException(nameof(recordSize));
        // FileRecordBuffer begins at offset 12, not the padded sizeof(C struct).
        if (output.Length < 12)
            throw new InvalidDataException("Truncated NTFS record response header.");
        ulong returned = BinaryPrimitives.ReadUInt64LittleEndian(output);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(output[8..]);
        if (returned > requestedIndex || length != recordSize || output.Length < 12 + recordSize)
            throw new InvalidDataException("Inconsistent NTFS record response.");
        return returned;
    }

    public ValueTask<ReadOnlyMemory<byte>> ReadAttributeListAsync(
        NonResidentAttribute attribute,
        CancellationToken cancellationToken
    ) =>
        new AttributeListValueReader(Volume.BytesPerCluster, Volume.TotalClusters).ReadAsync(
            this,
            attribute,
            cancellationToken
        );

    public ValueTask<ReadOnlyMemory<byte>> ReadClustersAsync(
        long startLcn,
        int clusterCount,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (
            startLcn < 0
            || startLcn >= Volume.TotalClusters
            || clusterCount <= 0
            || clusterCount > Volume.TotalClusters - startLcn
            || clusterCount > Math.Max(1, 65536 / Volume.BytesPerCluster)
        )
            throw new ArgumentOutOfRangeException(
                nameof(clusterCount),
                "Cluster read is outside the volume or acquisition limit."
            );
        long offset = checked(startLcn * Volume.BytesPerCluster);
        int count = checked(clusterCount * Volume.BytesPerCluster);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref rawReads);
            long start = Stopwatch.GetTimestamp();
            byte[] bytes;
            try
            {
                bytes = io.ReadVolumeBytes(offset, count, Volume.BytesPerSector);
            }
            finally
            {
                Interlocked.Add(ref acquisitionTicks, Stopwatch.GetTimestamp() - start);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes.Length != count)
                throw new IOException("Partial raw volume read.");
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes.AsMemory());
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            io.Dispose();
        }
    }
}

/// <summary>Native read timing only; excludes open/geometry, parsing, locking, and output.</summary>
public sealed record VolumeReadMetrics(
    long RecordRequests,
    long RawReads,
    TimeSpan AcquisitionTime
);
