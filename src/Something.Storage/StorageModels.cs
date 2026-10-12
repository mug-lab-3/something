using System.Buffers.Binary;
using Something.Ntfs;
using Something.Statistics;

namespace Something.Storage;

public enum StoredScanState
{
    Running,
    Completed,
    Cancelled,
    Failed,
    Interrupted,
}

/// <summary>Persistent VolumeKey requires validated volume identity, not a drive letter. A disposable single-volume diagnostic may use an invocation-scoped key. Epoch continuity is caller evidence.</summary>
public sealed record ScanDescriptor(
    string VolumeKey,
    Guid Epoch,
    ulong VolumeSerial,
    MftScanRange Range,
    DateTimeOffset StartedUtc
);

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
}

public sealed record StoredScan(
    Guid Id,
    ScanDescriptor Descriptor,
    StoredScanState State,
    DateTimeOffset? EndedUtc
);

internal static class StorageEncoding
{
    internal static byte[] Unsigned128(UInt128 value)
    {
        byte[] bytes = new byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, value);
        return bytes;
    }

    internal static UInt128 Unsigned128(byte[] bytes)
    {
        if (bytes.Length != 16)
            throw new InvalidDataException("Invalid aggregate capacity encoding.");
        return BinaryPrimitives.ReadUInt128BigEndian(bytes);
    }

    internal static byte[] Unsigned64(ulong value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    internal static ulong Unsigned64(byte[] bytes)
    {
        if (bytes.Length != 8)
            throw new InvalidDataException("Invalid volume serial encoding.");
        return BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    // Preserve UTF-16 code units, including names that cannot round-trip through SQLite UTF-8 TEXT.
    internal static byte[] Name(string value)
    {
        byte[] bytes = new byte[checked(value.Length * 2)];
        for (int i = 0; i < value.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), value[i]);
        return bytes;
    }

    internal static string Name(byte[] bytes)
    {
        if (bytes.Length % 2 != 0)
            throw new InvalidDataException("Invalid stored name encoding.");
        char[] chars = new char[bytes.Length / 2];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2));
        return new(chars);
    }
}
