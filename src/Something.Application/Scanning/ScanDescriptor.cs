using Something.Ntfs;
using Something.Statistics;

namespace Something.Application.Scanning;

/// <summary>Persistent VolumeKey requires validated volume identity, not a drive letter. A disposable single-volume diagnostic may use an invocation-scoped key. Epoch continuity is caller evidence.</summary>
public sealed record ScanDescriptor(
    string VolumeKey,
    Guid Epoch,
    ulong VolumeSerial,
    MftScanRange Range,
    DateTimeOffset StartedUtc
)
{
    public static void Validate(ScanDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (
            string.IsNullOrWhiteSpace(descriptor.VolumeKey)
            || descriptor.Epoch == Guid.Empty
            || descriptor.Range.RecordCount == 0
            || descriptor.StartedUtc.Offset != TimeSpan.Zero
        )
            throw new ArgumentException(
                "Scan requires a volume identity, epoch, nonempty range and UTC start.",
                nameof(descriptor)
            );
    }
}

public sealed record StagedObservation(
    ulong RecordIndex,
    MftScanEntryKind Kind,
    LiveFileReadStatus Status,
    FileCapacityObservation? File
)
{
    public static StagedObservation FromEntry(
        MftScanEntry entry,
        CancellationToken cancellationToken = default
    ) =>
        new(
            entry.RecordIndex,
            entry.Kind,
            entry.Status,
            MftObservationAdapter.Convert(entry, cancellationToken)
        );

    /// <summary>Rejects an observation outside <paramref name="range"/> or with states that disagree.</summary>
    public static void Validate(StagedObservation item, MftScanRange range)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (
            item.RecordIndex < range.StartIndex
            || item.RecordIndex >= range.EndExclusive
            || !Enum.IsDefined(item.Kind)
            || !Enum.IsDefined(item.Status)
        )
            throw new ArgumentException(
                "Observation lies outside the scan or has an invalid state.",
                nameof(item)
            );
        var file = item.File;
        if (file is null)
        {
            if (
                item.Kind == MftScanEntryKind.ResolvedFile
                || (
                    item.Kind == MftScanEntryKind.Unresolved
                    && item.Status == LiveFileReadStatus.ObservedUnchanged
                )
                || (
                    item.Kind == MftScanEntryKind.ExtensionRecord
                    && item.Status != LiveFileReadStatus.ObservedUnchanged
                )
            )
                throw new InvalidDataException("Staged kind and acquisition state disagree.");
            return;
        }
        if (
            file.Reference.RecordIndex != item.RecordIndex
            || !Enum.IsDefined(file.Domain)
            || !Enum.IsDefined(file.Resolution)
            || file.Names.IsDefault
            || file.Streams.IsDefault
        )
            throw new InvalidDataException("Invalid staged file observation.");
        if (
            item.Kind == MftScanEntryKind.ExtensionRecord
            || (
                item.Kind == MftScanEntryKind.ResolvedFile
                && (
                    item.Status != LiveFileReadStatus.ObservedUnchanged
                    || file.Resolution != CapacityResolution.Resolved
                )
            )
            || (
                item.Kind == MftScanEntryKind.Unresolved
                && (
                    item.Status == LiveFileReadStatus.ObservedUnchanged
                    || file.Resolution != CapacityResolution.Unresolved
                )
            )
            || (file.Resolution == CapacityResolution.Unresolved && !file.Streams.IsEmpty)
        )
            throw new InvalidDataException("Staged kind, state and capacity disagree.");
        foreach (var name in file.Names)
            if (name is null || string.IsNullOrEmpty(name.Name) || !Enum.IsDefined(name.Namespace))
                throw new InvalidDataException("Invalid staged name.");
    }
}
