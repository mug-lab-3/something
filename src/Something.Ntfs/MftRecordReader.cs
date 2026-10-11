using System.Buffers;
using System.Runtime.CompilerServices;

namespace Something.Ntfs;

public enum MftRecordReadStatus { Parsed, Uninitialized, Invalid }

public sealed record MftRecordReadResult(ulong RecordIndex, MftRecordReadStatus Status,
    MftRecord? Record, NtfsParseException? Error);

/// <summary>
/// Reads a raw $MFT stream (including deleted and zero slots). Handles partial reads and cancellation.
/// Owns neither the stream nor its lifetime. Live volume acquisition is a separate responsibility.
/// </summary>
public sealed class MftRecordReader
{
    private readonly int recordSize;
    private readonly MftRecordParser parser;

    public MftRecordReader(int recordSize, MftRecordParser? parser = null)
    {
        if (recordSize < MftRecordParser.FixupStride || recordSize > MftRecordParser.MaximumRecordSize ||
            !System.Numerics.BitOperations.IsPow2((uint)recordSize))
            throw new ArgumentOutOfRangeException(nameof(recordSize));
        this.recordSize = recordSize;
        this.parser = parser ?? new();
    }

    public async IAsyncEnumerable<MftRecordReadResult> ReadAsync(Stream stream, ulong firstRecordIndex = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstRecordIndex, FileReference.MaximumRecordIndex);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(recordSize);
        try
        {
            ulong index = firstRecordIndex;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = 0;
                while (count < recordSize)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(count, recordSize - count), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }
                if (count == 0) yield break;
                if (count != recordSize)
                {
                    yield return new(index, MftRecordReadStatus.Invalid, null,
                        new NtfsParseException("Truncated final record", count));
                    yield break;
                }
                if (buffer.AsSpan(0, recordSize).IndexOfAnyExcept((byte)0) == -1)
                    yield return new(index, MftRecordReadStatus.Uninitialized, null, null);
                else if (parser.TryParse(buffer.AsSpan(0, recordSize), index, out var record, out var error))
                    yield return new(index, MftRecordReadStatus.Parsed, record, null);
                else yield return new(index, MftRecordReadStatus.Invalid, null, error);
                if (index == FileReference.MaximumRecordIndex)
                    yield break;
                index++;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
