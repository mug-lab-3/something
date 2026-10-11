using System.Buffers.Binary;

namespace Something.Ntfs;

// All offsets are validated before slicing. Keep on-disk bytes out of unmanaged structures.
internal readonly ref struct BinaryView(ReadOnlySpan<byte> bytes, int origin = 0)
{
    private readonly ReadOnlySpan<byte> data = bytes;
    public int Length => data.Length;
    public ReadOnlySpan<byte> Slice(int offset, int length)
    {
        Require(offset, length);
        return data.Slice(offset, length);
    }
    public byte U8(int offset) => Slice(offset, 1)[0];
    public ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(offset, 2));
    public uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(offset, 4));
    public ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(Slice(offset, 8));
    public long I64(int offset) => BinaryPrimitives.ReadInt64LittleEndian(Slice(offset, 8));

    public void Require(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length || length > data.Length - offset)
            throw Error("Field extends outside its containing structure", offset);
    }

    public NtfsParseException Error(string message, int offset) => new(message, origin + Math.Clamp(offset, 0, data.Length));

    public int Length32(int offset)
    {
        uint value = U32(offset);
        if (value > int.MaxValue) throw Error("Length exceeds supported range", offset);
        return (int)value;
    }

    // NTFS names are UTF-16 code units. Preserve unpaired surrogates instead of replacing them.
    public string Name(int offset, int characters)
    {
        Require(offset, characters * 2);
        var chars = new char[characters];
        for (int i = 0; i < chars.Length; i++) chars[i] = (char)U16(offset + i * 2);
        return new string(chars);
    }
}
