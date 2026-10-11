namespace Something.Ntfs;

/// <summary>
/// Supplies one raw, protected record by index, or null if it no longer exists.
/// Returned memory must remain unchanged until the next ReadAsync call. Implementations own volume I/O,
/// alignment, retries for device errors, and their lifetime; operational I/O failures must throw.
/// </summary>
public interface IRawMftRecordSource
{
    ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
        ulong recordIndex,
        CancellationToken cancellationToken
    );
}

public enum LiveRecordReadStatus
{
    ObservedUnchanged,
    NotInUse,
    Missing,
    Replaced,
    ChangedDuringRead,
    Invalid,
}

public sealed record LiveRecordReadResult(
    LiveRecordReadStatus Status,
    MftRecord? Record,
    int Attempts,
    NtfsParseException? Error
);

/// <summary>
/// Bounded optimistic read: parse an owned copy, then reread to detect changes. This is not a snapshot
/// guarantee; a record can change after validation or change and revert between reads. Do not treat
/// missing/invalid/changed outcomes as zero-sized files or confirmed deletion from historical state.
/// </summary>
public sealed class LiveMftRecordReader
{
    private readonly int maximumAttempts;
    private readonly MftRecordParser parser;

    public LiveMftRecordReader(int maximumAttempts = 3, MftRecordParser? parser = null)
    {
        if (maximumAttempts is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        this.maximumAttempts = maximumAttempts;
        this.parser = parser ?? new();
    }

    public async ValueTask<LiveRecordReadResult> ReadAsync(
        IRawMftRecordSource source,
        ulong recordIndex,
        FileReference? expectedReference = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            recordIndex,
            FileReference.MaximumRecordIndex
        );
        if (expectedReference is { } expected && expected.RecordIndex != recordIndex)
            throw new ArgumentException(
                "Expected reference must refer to the requested index.",
                nameof(expectedReference)
            );
        NtfsParseException? lastError = null;
        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlyMemory<byte>? observed = await source
                .ReadAsync(recordIndex, cancellationToken)
                .ConfigureAwait(false);
            if (observed is null)
                return new(LiveRecordReadStatus.Missing, null, attempt, null);
            // Check before copying: an untrusted source cannot force an arbitrarily large allocation.
            if (observed.Value.Length > MftRecordParser.MaximumRecordSize)
                return new(
                    LiveRecordReadStatus.Invalid,
                    null,
                    attempt,
                    new("Record exceeds supported size", 0)
                );
            byte[] owned = observed.Value.ToArray();
            if (!parser.TryParse(owned, recordIndex, out var record, out lastError))
                continue;
            if (expectedReference is { } identity && record!.Reference != identity)
                return new(LiveRecordReadStatus.Replaced, null, attempt, null);
            ReadOnlyMemory<byte>? current = await source
                .ReadAsync(recordIndex, cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
                return new(LiveRecordReadStatus.Missing, null, attempt, null);
            if (!owned.AsSpan().SequenceEqual(current.Value.Span))
            {
                lastError = null;
                continue;
            }
            return new(
                record!.IsInUse
                    ? LiveRecordReadStatus.ObservedUnchanged
                    : LiveRecordReadStatus.NotInUse,
                record,
                attempt,
                null
            );
        }
        return new(
            lastError is null
                ? LiveRecordReadStatus.ChangedDuringRead
                : LiveRecordReadStatus.Invalid,
            null,
            maximumAttempts,
            lastError
        );
    }
}
