using System.Buffers.Binary;
using System.Numerics;

namespace Something.Windows;

internal static class StorageAlignment
{
    public static int Decode(ReadOnlySpan<byte> response, int expectedLogicalSector)
    {
        if (response.Length < 28)
            throw new InvalidDataException("Truncated storage alignment descriptor.");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(response);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(response[4..]);
        uint logical = BinaryPrimitives.ReadUInt32LittleEndian(response[16..]);
        uint physical = BinaryPrimitives.ReadUInt32LittleEndian(response[20..]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(response[24..]);
        if (
            version < 28
            || size < 28
            || size > response.Length
            || logical != expectedLogicalSector
            || physical < logical
            || physical > 65536
            || !BitOperations.IsPow2(physical)
            || offset >= physical
        )
            throw new InvalidDataException("Unsupported or inconsistent storage alignment.");
        return (int)physical;
    }
}
