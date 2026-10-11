using System.Collections.Immutable;

namespace Something.Ntfs;

/// <summary>Parses attribute values independently of where their bytes were read.</summary>
public static class AttributeValueParser
{
    public static NtfsFileName ParseFileName(ReadOnlySpan<byte> value)
    {
        var view = new BinaryView(value);
        view.Require(0, 66);
        byte characters = view.U8(64),
            nameSpace = view.U8(65);
        if (characters == 0 || nameSpace > 3)
            throw view.Error("Invalid file name", 64);
        if (value.Length != 66 + characters * 2)
            throw view.Error("File name value length mismatch", 64);
        return new(
            new(view.U64(0)),
            view.Name(66, characters),
            (FileNameNamespace)nameSpace,
            view.U32(56)
        );
        // Deliberately do not use the cached FILE_NAME sizes: these can be stale.
    }

    public static ImmutableArray<AttributeListEntry> ParseAttributeList(ReadOnlySpan<byte> value)
    {
        var entries = ImmutableArray.CreateBuilder<AttributeListEntry>();
        int offset = 0;
        while (offset < value.Length)
        {
            var view = new BinaryView(value[offset..], offset);
            view.Require(0, 26);
            uint type = view.U32(0);
            int length = view.U16(4),
                nameLength = view.U8(6),
                nameOffset = view.U8(7);
            if (type is 0 or uint.MaxValue || length < 26 || length % 8 != 0)
                throw view.Error("Invalid attribute list entry", 0);
            view.Require(0, length);
            var entry = new BinaryView(view.Slice(0, length), offset);
            if (nameLength > 0 && (nameOffset < 26 || nameOffset % 2 != 0))
                throw entry.Error("Invalid attribute list name offset", 7);
            long vcn = entry.I64(8);
            if (vcn < 0)
                throw entry.Error("Negative starting VCN", 8);
            entries.Add(
                new(
                    type,
                    entry.U16(24),
                    nameLength == 0 ? "" : entry.Name(nameOffset, nameLength),
                    vcn,
                    new(entry.U64(16))
                )
            );
            offset += length;
        }
        return entries.ToImmutable();
    }
}
