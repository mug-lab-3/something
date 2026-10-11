namespace Something.Ntfs;

public enum LiveFileReadStatus
{
    ObservedUnchanged,
    Missing,
    NotInUse,
    Replaced,
    ChangedDuringRead,
    InvalidRecord,
    InvalidAssembly,
    Unsupported,
    LimitExceeded,
}

/// <summary>Only ObservedUnchanged carries capacity; it remains an optimistic observation.</summary>
public sealed record LiveFileReadResult(
    LiveFileReadStatus Status,
    NtfsFile? File,
    int Attempts,
    ulong? UnresolvedRecordIndex = null,
    LiveRecordReadStatus? RecordStatus = null,
    string? Error = null
);

/// <summary>
/// Acquires resident ATTRIBUTE_LIST references, assembles, then revalidates every acquired record.
/// Nonresident lists and lists in extension records are unsupported, never partial capacity.
/// Retries and record count are bounded. I/O errors and cancellation propagate to the caller.
/// No volume writes, raw extent reads, or point-in-time snapshot guarantees.
/// </summary>
public sealed class LiveMftFileReader
{
    private readonly MftFileAssembler assembler;
    private readonly LiveMftRecordReader recordReader = new();
    private readonly int maximumAttempts;
    private readonly int maximumRecords;

    public LiveMftFileReader(int bytesPerCluster, int maximumAttempts = 3, int maximumRecords = 128)
    {
        if (maximumAttempts is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        if (maximumRecords is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        assembler = new(bytesPerCluster);
        this.maximumAttempts = maximumAttempts;
        this.maximumRecords = maximumRecords;
    }

    public async ValueTask<LiveFileReadResult> ReadAsync(
        IMftRecordSource source,
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
        FileReference? identity = expectedReference;
        ulong changedIndex = recordIndex;
        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = await recordReader
                .ReadAsync(source, recordIndex, identity, cancellationToken)
                .ConfigureAwait(false);
            if (first.Status == LiveRecordReadStatus.ChangedDuringRead)
            {
                changedIndex = recordIndex;
                continue;
            }
            if (first.Status != LiveRecordReadStatus.ObservedUnchanged)
                return Failure(first, recordIndex, attempt);
            MftRecord baseRecord = first.Record!;
            identity ??= baseRecord.Reference;
            if (baseRecord.IsExtension)
                return new(
                    LiveFileReadStatus.InvalidAssembly,
                    null,
                    attempt,
                    recordIndex,
                    Error: "Expected a base file record."
                );

            if (NtfsMetadataClassifier.Classify(baseRecord) == NtfsMetadataKind.BadClusters)
                return new(
                    LiveFileReadStatus.Unsupported,
                    null,
                    attempt,
                    recordIndex,
                    Error: "Dedicated capacity accounting for NTFS $BadClus metadata is not implemented."
                );

            var lists = baseRecord
                .Attributes.Where(a => a.Type == NtfsAttributeTypes.AttributeList)
                .ToArray();
            if (lists.Length > 1 || (lists.Length == 1 && lists[0].Name.Length != 0))
                return new(
                    LiveFileReadStatus.InvalidAssembly,
                    null,
                    attempt,
                    recordIndex,
                    Error: "Invalid ATTRIBUTE_LIST declaration."
                );
            if (lists is [NonResidentAttribute])
                return new(
                    LiveFileReadStatus.Unsupported,
                    null,
                    attempt,
                    recordIndex,
                    Error: "Nonresident ATTRIBUTE_LIST acquisition is not implemented."
                );

            FileReference[] references = [];
            if (lists is [ResidentAttribute list])
            {
                // The record parser already validated this immutable resident value.
                references = AttributeValueParser
                    .ParseAttributeList(list.Value.AsSpan())
                    .Select(e => e.Record)
                    .Distinct()
                    .OrderBy(r => r.RecordIndex)
                    .ToArray();
                if (
                    references.GroupBy(r => r.RecordIndex).Any(g => g.Count() != 1)
                    || references.Any(r =>
                        r.RecordIndex == recordIndex && r != baseRecord.Reference
                    )
                )
                    return new(
                        LiveFileReadStatus.InvalidAssembly,
                        null,
                        attempt,
                        recordIndex,
                        Error: "Conflicting ATTRIBUTE_LIST identities."
                    );
            }
            var extensions = references.Where(r => r != baseRecord.Reference).ToArray();
            if (extensions.Length >= maximumRecords)
                return new(
                    LiveFileReadStatus.LimitExceeded,
                    null,
                    attempt,
                    recordIndex,
                    Error: "File record acquisition limit exceeded."
                );

            List<LiveRecordReadResult> observations = [first];
            bool changed = false;
            foreach (var reference in extensions)
            {
                var observed = await recordReader
                    .ReadAsync(source, reference.RecordIndex, reference, cancellationToken)
                    .ConfigureAwait(false);
                if (observed.Status == LiveRecordReadStatus.ChangedDuringRead)
                {
                    changedIndex = reference.RecordIndex;
                    changed = true;
                    break;
                }
                if (observed.Status != LiveRecordReadStatus.ObservedUnchanged)
                    return Failure(observed, reference.RecordIndex, attempt);
                if (
                    observed.Record!.Attributes.Any(a => a.Type == NtfsAttributeTypes.AttributeList)
                )
                    return new(
                        LiveFileReadStatus.Unsupported,
                        null,
                        attempt,
                        reference.RecordIndex,
                        Error: "ATTRIBUTE_LIST in an extension record is not supported."
                    );
                observations.Add(observed);
            }
            if (changed)
                continue;

            NtfsFile? file = null;
            string? assemblyError = null;
            try
            {
                file = assembler.Assemble(baseRecord, observations.Skip(1).Select(o => o.Record!));
            }
            catch (Exception error) when (error is NtfsAssemblyException or NtfsParseException)
            {
                assemblyError = error.Message;
            }
            // Even an assembly error may be caused by mixing observations of a changing file.
            // Validate its inputs before classifying the structure as unresolved/invalid.
            foreach (var observed in observations)
            {
                var reference = observed.Record!.Reference;
                var verified = await recordReader
                    .ReadAsync(source, reference.RecordIndex, reference, cancellationToken)
                    .ConfigureAwait(false);
                if (verified.Status == LiveRecordReadStatus.ChangedDuringRead)
                {
                    changed = true;
                    changedIndex = reference.RecordIndex;
                    break;
                }
                if (verified.Status != LiveRecordReadStatus.ObservedUnchanged)
                    return Failure(verified, reference.RecordIndex, attempt);
                if (!observed.ObservedBytes.AsSpan().SequenceEqual(verified.ObservedBytes.AsSpan()))
                {
                    changed = true;
                    changedIndex = reference.RecordIndex;
                    break;
                }
            }
            if (changed)
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            return assemblyError is null
                ? new(LiveFileReadStatus.ObservedUnchanged, file, attempt)
                : new(
                    LiveFileReadStatus.InvalidAssembly,
                    null,
                    attempt,
                    recordIndex,
                    Error: assemblyError
                );
        }
        return new(LiveFileReadStatus.ChangedDuringRead, null, maximumAttempts, changedIndex);
    }

    private static LiveFileReadResult Failure(
        LiveRecordReadResult result,
        ulong index,
        int attempt
    ) =>
        new(
            result.Status switch
            {
                LiveRecordReadStatus.Missing => LiveFileReadStatus.Missing,
                LiveRecordReadStatus.NotInUse => LiveFileReadStatus.NotInUse,
                LiveRecordReadStatus.Replaced => LiveFileReadStatus.Replaced,
                LiveRecordReadStatus.ChangedDuringRead => LiveFileReadStatus.ChangedDuringRead,
                _ => LiveFileReadStatus.InvalidRecord,
            },
            null,
            attempt,
            index,
            result.Status,
            result.Error?.Message
        );
}
