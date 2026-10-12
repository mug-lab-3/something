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
/// Single-extent nonresident lists require IAttributeListValueSource; extension lists are unsupported.
/// Retries and record count are bounded. I/O errors and cancellation propagate to the caller.
/// No volume writes or point-in-time snapshot guarantees.
/// </summary>
public sealed class LiveMftFileReader
{
    public const int DefaultMaximumRecords = 1024;

    private readonly MftFileAssembler assembler;
    private readonly LiveMftRecordReader recordReader = new();
    private readonly int maximumAttempts;
    private readonly int maximumRecords;

    public LiveMftFileReader(
        int bytesPerCluster,
        int maximumAttempts = 3,
        int maximumRecords = DefaultMaximumRecords,
        long? totalClusters = null
    )
    {
        if (maximumAttempts is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRecords);
        assembler = new(bytesPerCluster, totalClusters);
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
            changedIndex = recordIndex;
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
            NonResidentAttribute? externalList = lists is [NonResidentAttribute nonresident]
                ? nonresident
                : null;
            ReadOnlyMemory<byte>? suppliedList = null;
            if (externalList is not null)
            {
                if (source is not IAttributeListValueSource listSource)
                    return new(
                        LiveFileReadStatus.Unsupported,
                        null,
                        attempt,
                        recordIndex,
                        Error: "The record source cannot acquire nonresident ATTRIBUTE_LIST values."
                    );
                try
                {
                    suppliedList = await ReadListOwnedAsync(
                            listSource,
                            externalList,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    var second = await ReadListOwnedAsync(
                            listSource,
                            externalList,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    if (!suppliedList.Value.Span.SequenceEqual(second.Span))
                    {
                        changedIndex = recordIndex;
                        continue;
                    }
                    var anchor = await recordReader
                        .ReadAsync(source, recordIndex, baseRecord.Reference, cancellationToken)
                        .ConfigureAwait(false);
                    if (anchor.Status == LiveRecordReadStatus.ChangedDuringRead)
                        continue;
                    if (anchor.Status != LiveRecordReadStatus.ObservedUnchanged)
                        return Failure(anchor, recordIndex, attempt);
                    if (!first.ObservedBytes.AsSpan().SequenceEqual(anchor.ObservedBytes.AsSpan()))
                        continue;
                }
                catch (AttributeListReadException error)
                {
                    return ListFailure(error, recordIndex, attempt);
                }
            }

            FileReference[] references = [];
            ReadOnlyMemory<byte>? listValue =
                suppliedList
                ?? (
                    lists is [ResidentAttribute list]
                        ? list.Value.AsMemory()
                        : (ReadOnlyMemory<byte>?)null
                );
            if (listValue is { } value)
            {
                try
                {
                    references = AttributeValueParser
                        .ParseAttributeList(value.Span)
                        .Select(e => e.Record)
                        .Distinct()
                        .OrderBy(r => r.RecordIndex)
                        .ToArray();
                }
                catch (NtfsParseException error)
                {
                    return new(
                        LiveFileReadStatus.InvalidAssembly,
                        null,
                        attempt,
                        recordIndex,
                        Error: error.Message
                    );
                }
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
            // The base record counts even when ATTRIBUTE_LIST does not reference it.
            // Multiple attributes in one extension consume only one record acquisition slot.
            int requiredRecords = checked(extensions.Length + 1);
            if (requiredRecords > maximumRecords)
                return new(
                    LiveFileReadStatus.LimitExceeded,
                    null,
                    attempt,
                    recordIndex,
                    Error: $"File record acquisition limit exceeded: required-records={requiredRecords}; maximum-records={maximumRecords}; extension-records={extensions.Length}."
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
            var assemblyStatus = LiveFileReadStatus.InvalidAssembly;
            try
            {
                file = assembler.Assemble(
                    baseRecord,
                    observations.Skip(1).Select(o => o.Record!),
                    suppliedList
                );
            }
            catch (Exception error) when (error is NtfsAssemblyException or NtfsParseException)
            {
                assemblyError = error.Message;
                if (error is NtfsAssemblyException { Kind: NtfsAssemblyFailureKind.Unsupported })
                    assemblyStatus = LiveFileReadStatus.Unsupported;
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
            if (externalList is not null)
            {
                try
                {
                    var finalList = await ReadListOwnedAsync(
                            (IAttributeListValueSource)source,
                            externalList,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    if (!suppliedList!.Value.Span.SequenceEqual(finalList.Span))
                    {
                        changedIndex = recordIndex;
                        continue;
                    }
                    // Recheck the descriptor after the final physical read; freed clusters may be reused.
                    var anchor = await recordReader
                        .ReadAsync(source, recordIndex, baseRecord.Reference, cancellationToken)
                        .ConfigureAwait(false);
                    if (anchor.Status == LiveRecordReadStatus.ChangedDuringRead)
                        continue;
                    if (anchor.Status != LiveRecordReadStatus.ObservedUnchanged)
                        return Failure(anchor, recordIndex, attempt);
                    if (!first.ObservedBytes.AsSpan().SequenceEqual(anchor.ObservedBytes.AsSpan()))
                        continue;
                }
                catch (AttributeListReadException error)
                {
                    return ListFailure(error, recordIndex, attempt);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return assemblyError is null
                ? new(LiveFileReadStatus.ObservedUnchanged, file, attempt)
                : new(assemblyStatus, null, attempt, recordIndex, Error: assemblyError);
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

    private static async ValueTask<ReadOnlyMemory<byte>> ReadListOwnedAsync(
        IAttributeListValueSource source,
        NonResidentAttribute attribute,
        CancellationToken token
    )
    {
        if (
            attribute.Sizes is not { } sizes
            || sizes.LogicalBytes <= 0
            || sizes.InitializedBytes != sizes.LogicalBytes
        )
            throw new AttributeListReadException(
                AttributeListReadFailure.Invalid,
                "Invalid ATTRIBUTE_LIST logical or initialized length."
            );
        if (sizes.LogicalBytes > AttributeListValueReader.MaximumValueBytes)
            throw new AttributeListReadException(
                AttributeListReadFailure.LimitExceeded,
                "ATTRIBUTE_LIST value exceeds the acquisition limit."
            );
        var bytes = await source.ReadAttributeListAsync(attribute, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (bytes.Length != sizes.LogicalBytes)
            throw new AttributeListReadException(
                AttributeListReadFailure.Invalid,
                "ATTRIBUTE_LIST response length does not match its descriptor."
            );
        return bytes.ToArray();
    }

    private static LiveFileReadResult ListFailure(
        AttributeListReadException error,
        ulong index,
        int attempt
    ) =>
        new(
            error.Failure switch
            {
                AttributeListReadFailure.Unsupported => LiveFileReadStatus.Unsupported,
                AttributeListReadFailure.LimitExceeded => LiveFileReadStatus.LimitExceeded,
                _ => LiveFileReadStatus.InvalidAssembly,
            },
            null,
            attempt,
            index,
            Error: error.Message
        );
}
