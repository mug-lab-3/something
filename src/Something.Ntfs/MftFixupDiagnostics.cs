using System.Buffers.Binary;
using System.Numerics;

namespace Something.Ntfs;

/// <summary>Structural counts only. This is not an encoding detector or integrity verdict.</summary>
public sealed record MftFixupSummary(
    int SectorCount,
    int ProtectedTailMatches,
    int SavedTailMatches,
    int OtherTails,
    int SavedMismatchesWithinUsedBytes,
    int SavedMismatchesOutsideUsedBytes
);

public static class MftFixupDiagnostics
{
    /// <summary>
    /// Returns null for an invalid diagnostic header; never restores bytes or chooses an encoding.
    /// Match counts can overlap when a saved word equals the update sequence number.
    /// </summary>
    public static MftFixupSummary? Inspect(ReadOnlySpan<byte> bytes)
    {
        if (
            bytes.Length is < 512 or > MftRecordParser.MaximumRecordSize
            || !BitOperations.IsPow2((uint)bytes.Length)
            || !bytes[..4].SequenceEqual("FILE"u8)
        )
            return null;
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        int words = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        uint used = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]);
        if (
            words != bytes.Length / MftRecordParser.FixupStride + 1
            || offset < 42
            || offset % 2 != 0
            || offset + words * 2 > MftRecordParser.FixupStride - 2
            || used < offset + words * 2
            || used > bytes.Length
        )
            return null;
        ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
        int protectedMatches = 0,
            savedMatches = 0,
            other = 0,
            inside = 0,
            outside = 0;
        for (int i = 1; i < words; i++)
        {
            int tailOffset = i * MftRecordParser.FixupStride - 2;
            ushort tail = BinaryPrimitives.ReadUInt16LittleEndian(bytes[tailOffset..]);
            ushort saved = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + i * 2)..]);
            if (tail == sequence)
                protectedMatches++;
            if (tail == saved)
                savedMatches++;
            else if (tailOffset < used)
                inside++;
            else
                outside++;
            if (tail != sequence && tail != saved)
                other++;
        }
        return new(words - 1, protectedMatches, savedMatches, other, inside, outside);
    }
}
