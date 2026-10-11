using System.Buffers.Binary;
using System.Text;
using Something.Ntfs;

namespace Something.Ntfs.Tests;

// Synthetic fixtures; no private disk contents. Encoding is intentionally independent of production code.
internal static class RecordFixture
{
    public static void U16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    public static void U32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    public static void U64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
    public static void I64(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);
    private static int Align(int value) => (value + 7) & ~7;

    public static byte[] Resident(uint type, ushort id, byte[] value, string name = "")
    {
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        int valueOffset = Align(24 + nameBytes.Length);
        byte[] attribute = new byte[Align(valueOffset + value.Length)];
        U32(attribute, 0, type); U32(attribute, 4, (uint)attribute.Length);
        attribute[9] = (byte)name.Length; U16(attribute, 10, 24); U16(attribute, 14, id);
        U32(attribute, 16, (uint)value.Length); U16(attribute, 20, (ushort)valueOffset);
        nameBytes.CopyTo(attribute, 24); value.CopyTo(attribute, valueOffset);
        return attribute;
    }

    public static byte[] NonResident(ushort id = 1, string name = "", long lowestVcn = 0,
        long highestVcn = 1, byte[]? runs = null, NtfsAttributeFlags flags = 0, long logical = 5000,
        long allocated = 8192, long? physical = null, uint type = NtfsAttributeTypes.Data)
    {
        runs ??= [0x11, 2, 100, 0];
        int minimum = lowestVcn == 0 && (flags & (NtfsAttributeFlags.Compressed | NtfsAttributeFlags.Sparse)) != 0 ? 72 : 64;
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        int runOffset = Align(minimum + nameBytes.Length);
        byte[] attribute = new byte[Align(runOffset + runs.Length)];
        U32(attribute, 0, type); U32(attribute, 4, (uint)attribute.Length); attribute[8] = 1;
        attribute[9] = (byte)name.Length; U16(attribute, 10, (ushort)minimum);
        U16(attribute, 12, (ushort)flags); U16(attribute, 14, id);
        I64(attribute, 16, lowestVcn); I64(attribute, 24, highestVcn);
        U16(attribute, 32, (ushort)runOffset);
        I64(attribute, 40, allocated); I64(attribute, 48, logical); I64(attribute, 56, logical);
        if (minimum == 72) I64(attribute, 64, physical ?? allocated);
        nameBytes.CopyTo(attribute, minimum); runs.CopyTo(attribute, runOffset);
        return attribute;
    }

    public static byte[] FileName(string name, FileNameNamespace nameSpace = FileNameNamespace.Win32,
        ulong parent = 0x0002_0000_0000_0005)
    {
        byte[] bytes = new byte[66 + name.Length * 2];
        U64(bytes, 0, parent);
        // Cached FILE_NAME sizes must not be mistaken for current DATA sizes.
        U64(bytes, 40, 999999); U64(bytes, 48, 888888);
        bytes[64] = (byte)name.Length; bytes[65] = (byte)nameSpace;
        for (int i = 0; i < name.Length; i++) U16(bytes, 66 + i * 2, name[i]);
        return bytes;
    }

    public static byte[] ListEntry(uint type, ushort id, FileReference reference, long vcn = 0, string name = "")
    {
        byte[] bytes = new byte[Align(26 + name.Length * 2)];
        U32(bytes, 0, type); U16(bytes, 4, (ushort)bytes.Length);
        bytes[6] = (byte)name.Length; bytes[7] = 26;
        I64(bytes, 8, vcn); U64(bytes, 16, reference.Value); U16(bytes, 24, id);
        Encoding.Unicode.GetBytes(name).CopyTo(bytes, 26);
        return bytes;
    }

    public static byte[] Record(byte[][]? attributes = null, int size = 1024,
        MftRecordFlags flags = MftRecordFlags.InUse, ulong baseReference = 0, uint index = 42)
    {
        attributes ??= [Resident(NtfsAttributeTypes.FileName, 0, FileName("hello.txt")), NonResident()];
        byte[] bytes = new byte[size];
        "FILE"u8.CopyTo(bytes);
        int words = size / 512 + 1, first = Align(48 + words * 2);
        U16(bytes, 4, 48); U16(bytes, 6, (ushort)words);
        U16(bytes, 16, 3); U16(bytes, 18, 1); U16(bytes, 20, (ushort)first);
        U16(bytes, 22, (ushort)flags); U32(bytes, 28, (uint)size);
        U64(bytes, 32, baseReference); U32(bytes, 44, index);
        int offset = first;
        foreach (byte[] attribute in attributes) { attribute.CopyTo(bytes, offset); offset += attribute.Length; }
        U32(bytes, offset, uint.MaxValue); U32(bytes, 24, (uint)(offset + 8));
        Protect(bytes);
        return bytes;
    }

    public static void Protect(byte[] bytes)
    {
        U16(bytes, 48, 0xABCD);
        for (int i = 1; i <= bytes.Length / 512; i++)
        {
            bytes.AsSpan(i * 512 - 2, 2).CopyTo(bytes.AsSpan(48 + i * 2, 2));
            U16(bytes, i * 512 - 2, 0xABCD);
        }
    }
}
