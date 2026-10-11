using System.Buffers.Binary;
using System.Numerics;
using Something.Ntfs;

namespace Something.Windows;

/// <summary>Volume metadata observed at open time, not a live size or identity guarantee.</summary>
public sealed record NtfsVolumeInfo(
    ulong SerialNumber,
    long TotalClusters,
    long FreeClusters,
    int BytesPerSector,
    int BytesPerCluster,
    int BytesPerRecord,
    long MftValidDataLength
)
{
    public ulong ObservedRecordCount => (ulong)(MftValidDataLength / BytesPerRecord);

    internal static NtfsVolumeInfo Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 96)
            throw new InvalidDataException("Truncated NTFS volume response.");
        ulong serial = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        long total = BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]);
        long free = BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]);
        uint sector = BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..]);
        uint cluster = BinaryPrimitives.ReadUInt32LittleEndian(bytes[44..]);
        uint record = BinaryPrimitives.ReadUInt32LittleEndian(bytes[48..]);
        long mft = BinaryPrimitives.ReadInt64LittleEndian(bytes[56..]);
        if (
            sector is < 512 or > 4096
            || !BitOperations.IsPow2(sector)
            || cluster < sector
            || cluster > 2 * 1024 * 1024
            || !BitOperations.IsPow2(cluster)
            || record is < 512 or > MftRecordParser.MaximumRecordSize
            || !BitOperations.IsPow2(record)
            || total <= 0
            || free < 0
            || free > total
            || total > long.MaxValue / cluster
            || mft <= 0
            || mft % record != 0
            || mft > total * cluster
            || (ulong)(mft / record) > FileReference.MaximumRecordIndex + 1
        )
            throw new InvalidDataException("Unsupported or inconsistent NTFS volume geometry.");
        return new(serial, total, free, (int)sector, (int)cluster, (int)record, mft);
    }
}
