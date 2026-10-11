using System.Runtime.CompilerServices;

namespace Something.Ntfs;

/// <summary>Fixed slot range observed at scan start. New slots outside it are not scanned.</summary>
public readonly record struct MftScanRange
{
    public ulong StartIndex { get; }
    public ulong RecordCount { get; }
    public ulong EndExclusive => StartIndex + RecordCount;

    public MftScanRange(ulong startIndex, ulong recordCount)
    {
        if (
            startIndex > FileReference.MaximumRecordIndex
            || recordCount == 0
            || recordCount > FileReference.MaximumRecordIndex + 1 - startIndex
        )
            throw new ArgumentOutOfRangeException(nameof(recordCount));
        StartIndex = startIndex;
        RecordCount = recordCount;
    }
}

/// <summary>Memory remains unchanged until the source's next acquisition call.</summary>
public sealed record MftRecordCandidate(ulong RecordIndex, ReadOnlyMemory<byte> Bytes);

public interface IMftScanSource : IMftRecordSource
{
    /// <summary>
    /// Enumerates candidates in strictly descending slot order. Gaps are observations, not deletions.
    /// Operational failures throw; they must not silently end enumeration.
    /// </summary>
    IAsyncEnumerable<MftRecordCandidate> EnumerateAsync(
        MftScanRange range,
        CancellationToken cancellationToken
    );
}

public enum MftScanEntryKind
{
    ResolvedFile,
    ExtensionRecord,
    Unresolved,
}

/// <summary>Extension records carry no separately countable capacity.</summary>
public sealed record MftScanEntry(
    ulong RecordIndex,
    MftScanEntryKind Kind,
    LiveFileReadStatus Status,
    NtfsFile? File = null,
    string? Error = null
);

/// <summary>
/// Streams complete optimistic file observations without retaining the volume's records.
/// An enumeration identity is revalidated before assembly. No disk writes or snapshot guarantee.
/// </summary>
public sealed class LiveMftScanner(int bytesPerCluster)
{
    private readonly LiveMftFileReader fileReader = new(bytesPerCluster);
    private readonly LiveMftRecordReader recordReader = new();
    private readonly MftRecordParser parser = new();

    public async IAsyncEnumerable<MftScanEntry> ScanAsync(
        IMftScanSource source,
        MftScanRange range,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        if (range.RecordCount == 0)
            throw new ArgumentException("Scan range must not be empty.", nameof(range));
        ulong previous = range.EndExclusive;
        await foreach (
            var candidate in source
                .EnumerateAsync(range, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false)
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong index = candidate.RecordIndex;
            if (index < range.StartIndex || index >= previous)
                throw new InvalidDataException(
                    "Scan source returned an out-of-range, repeated, or unordered record."
                );
            previous = index;
            // Parse before any other source call can reuse its candidate buffer.
            FileReference? expected = null;
            MftRecord? record = null;
            if (
                candidate.Bytes.Length <= MftRecordParser.MaximumRecordSize
                && parser.TryParse(candidate.Bytes.Span, index, out record, out _, source.Encoding)
            )
                expected = record!.Reference;

            if (record is null || !record.IsInUse || record.IsExtension)
            {
                var observed = await recordReader
                    .ReadAsync(source, index, expected, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (observed.Status != LiveRecordReadStatus.ObservedUnchanged)
                {
                    yield return new(
                        index,
                        MftScanEntryKind.Unresolved,
                        Map(observed.Status),
                        Error: observed.Error?.Message
                    );
                    continue;
                }
                record = observed.Record!;
                expected = record.Reference;
                if (record.IsExtension)
                {
                    yield return new(
                        index,
                        MftScanEntryKind.ExtensionRecord,
                        LiveFileReadStatus.ObservedUnchanged
                    );
                    continue;
                }
            }

            var result = await fileReader
                .ReadAsync(source, index, expected, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(
                index,
                result.File is null ? MftScanEntryKind.Unresolved : MftScanEntryKind.ResolvedFile,
                result.Status,
                result.File,
                result.Error
            );
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static LiveFileReadStatus Map(LiveRecordReadStatus status) =>
        status switch
        {
            LiveRecordReadStatus.Missing => LiveFileReadStatus.Missing,
            LiveRecordReadStatus.NotInUse => LiveFileReadStatus.NotInUse,
            LiveRecordReadStatus.Replaced => LiveFileReadStatus.Replaced,
            LiveRecordReadStatus.ChangedDuringRead => LiveFileReadStatus.ChangedDuringRead,
            _ => LiveFileReadStatus.InvalidRecord,
        };
}
