using System.Buffers;
using System.Collections.Immutable;

namespace Something.Ntfs;

/// <summary>
/// Parses NTFS 3.x FILE records in an explicit raw or decoded encoding. No I/O or input mutation.
/// Windows-decoded records cannot prove on-disk fixup integrity.
/// </summary>
public sealed class MftRecordParser
{
    public const int FixupStride = 512;
    public const int MaximumRecordSize = 64 * 1024;

    public MftRecord Parse(
        ReadOnlySpan<byte> rawRecord,
        ulong recordIndex,
        MftRecordEncoding encoding = MftRecordEncoding.RawProtected
    )
    {
        if (
            encoding
            is not (
                MftRecordEncoding.RawProtected
                or MftRecordEncoding.FixupsRestored
                or MftRecordEncoding.WindowsDecoded
            )
        )
            throw new ArgumentOutOfRangeException(nameof(encoding));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            recordIndex,
            FileReference.MaximumRecordIndex
        );
        if (
            rawRecord.Length < FixupStride
            || rawRecord.Length > MaximumRecordSize
            || !System.Numerics.BitOperations.IsPow2((uint)rawRecord.Length)
        )
            throw new NtfsParseException(
                "Record size must be a power of two between 512 and 65536",
                0
            );

        byte[] rented = ArrayPool<byte>.Shared.Rent(rawRecord.Length);
        try
        {
            Span<byte> record = rented.AsSpan(0, rawRecord.Length);
            rawRecord.CopyTo(record);
            RestoreFixups(record, encoding);
            return ParseRestored(record, recordIndex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    public bool TryParse(
        ReadOnlySpan<byte> rawRecord,
        ulong recordIndex,
        out MftRecord? record,
        out NtfsParseException? error,
        MftRecordEncoding encoding = MftRecordEncoding.RawProtected
    )
    {
        try
        {
            record = Parse(rawRecord, recordIndex, encoding);
            error = null;
            return true;
        }
        catch (NtfsParseException exception)
        {
            record = null;
            error = exception;
            return false;
        }
    }

    private static void RestoreFixups(Span<byte> record, MftRecordEncoding encoding)
    {
        var view = new BinaryView(record);
        if (!view.Slice(0, 4).SequenceEqual("FILE"u8))
            throw view.Error("Missing FILE signature", 0);
        int offset = view.U16(4),
            words = view.U16(6);
        // The on-disk field is a count of USHORTs, including the update sequence number.
        if (words != record.Length / FixupStride + 1)
            throw view.Error("Update sequence array count mismatch", 6);
        if (offset < 42 || offset % 2 != 0 || offset + words * 2 > FixupStride - 2)
            throw view.Error("Invalid update sequence array location", 4);
        view.Require(offset, words * 2);
        // The Windows API returns filesystem-decoded bytes, not a protected disk image.
        // Keep USA bounds checks but do not restore old saved words over current resident data.
        // This contract conveys no on-disk integrity proof; raw acquisition must use RawProtected.
        if (encoding == MftRecordEncoding.WindowsDecoded)
            return;
        ushort sequence = view.U16(offset);
        // Validate all tails before restoring any: never accept a partially torn record.
        for (int i = 1; i < words; i++)
            if (
                view.U16(i * FixupStride - 2)
                != (
                    encoding == MftRecordEncoding.RawProtected ? sequence : view.U16(offset + i * 2)
                )
            )
                throw view.Error(
                    "Update sequence mismatch for the specified record encoding",
                    i * FixupStride - 2
                );
        if (encoding == MftRecordEncoding.RawProtected)
            for (int i = 1; i < words; i++)
                view.Slice(offset + i * 2, 2).CopyTo(record.Slice(i * FixupStride - 2, 2));
    }

    private static MftRecord ParseRestored(ReadOnlySpan<byte> record, ulong recordIndex)
    {
        var view = new BinaryView(record);
        int firstAttribute = view.U16(20),
            used = view.Length32(24),
            allocated = view.Length32(28);
        int usaEnd = view.U16(4) + view.U16(6) * 2;
        if (allocated != record.Length)
            throw view.Error("Allocated record size mismatch", 28);
        if (firstAttribute < 48 || firstAttribute % 8 != 0 || firstAttribute < usaEnd)
            throw view.Error("Invalid first attribute offset", 20);
        if (used > allocated || used < firstAttribute + 4)
            throw view.Error("Invalid used record size", 24);

        var flags = (MftRecordFlags)view.U16(22);
        var attributes = ImmutableArray.CreateBuilder<NtfsAttribute>();
        // Deleted segments retain stale attribute bytes. Expose identity/status only.
        if ((flags & MftRecordFlags.InUse) != 0)
        {
            int offset = firstAttribute;
            var usedView = new BinaryView(record[..used]);
            var ids = new HashSet<ushort>();
            while (true)
            {
                uint type = usedView.U32(offset);
                if (type == uint.MaxValue)
                    break;
                usedView.Require(offset, 16);
                int length = usedView.Length32(offset + 4);
                if (type == 0 || length < 24 || length % 8 != 0)
                    throw usedView.Error("Invalid attribute type or length", offset);
                usedView.Require(offset, length);
                NtfsAttribute attribute = ParseAttribute(usedView.Slice(offset, length), offset);
                if (!ids.Add(attribute.Id))
                    throw usedView.Error("Duplicate attribute instance ID", offset + 14);
                attributes.Add(attribute);
                offset += length;
            }
        }
        return new(
            FileReference.Create(recordIndex, view.U16(16)),
            new(view.U64(32)),
            flags,
            view.U16(18),
            view.U64(8),
            attributes.ToImmutable()
        );
    }

    private static NtfsAttribute ParseAttribute(ReadOnlySpan<byte> bytes, int origin)
    {
        var view = new BinaryView(bytes, origin);
        uint type = view.U32(0);
        ushort id = view.U16(14);
        var flags = (NtfsAttributeFlags)view.U16(12);
        byte form = view.U8(8),
            nameLength = view.U8(9);
        int nameOffset = view.U16(10);
        int minimum = form switch
        {
            0 => 24,
            1 => 64,
            _ => throw view.Error("Unknown attribute form", 8),
        };
        view.Require(0, minimum);
        // Continuation extents can have a 64-byte header even for compressed/sparse streams.
        if (
            form == 1
            && view.I64(16) == 0
            && (flags & (NtfsAttributeFlags.Compressed | NtfsAttributeFlags.Sparse)) != 0
        )
            minimum = 72;
        view.Require(0, minimum);
        if (nameLength != 0 && (nameOffset < minimum || nameOffset % 2 != 0))
            throw view.Error("Invalid attribute name offset", 10);
        string name = nameLength == 0 ? "" : view.Name(nameOffset, nameLength);
        int headerEnd = nameLength == 0 ? minimum : nameOffset + nameLength * 2;

        if (form == 0)
        {
            int valueLength = view.Length32(16),
                valueOffset = view.U16(20);
            if (valueOffset < headerEnd)
                throw view.Error("Resident value overlaps attribute header/name", 20);
            ReadOnlySpan<byte> value = view.Slice(valueOffset, valueLength);
            if (type == NtfsAttributeTypes.StandardInformation && (name.Length != 0 || flags != 0))
                throw view.Error("STANDARD_INFORMATION must be unnamed and uncompressed", 8);
            ValidateKnownValue(type, value, origin + valueOffset);
            return new ResidentAttribute(
                type,
                id,
                name,
                flags,
                ImmutableArray.Create(value.ToArray())
            );
        }

        if (type is NtfsAttributeTypes.FileName or NtfsAttributeTypes.StandardInformation)
            throw view.Error("FILE_NAME and STANDARD_INFORMATION must be resident", 8);
        long lowest = view.I64(16),
            highest = view.I64(24);
        int pairsOffset = view.U16(32);
        // NTFS3 v6.12 and ntfs-3g 2022.10.3 describe a byte followed by five reserved bytes.
        // Preserve the value; capacity accounting does not decompress or shift by this field.
        byte compressionUnit = view.U8(34);
        if (pairsOffset < headerEnd)
            throw view.Error("Mapping pairs overlap attribute header/name", 32);
        view.Require(pairsOffset, 1);
        StreamSizes? sizes = null;
        if (lowest == 0)
        {
            long allocated = view.I64(40),
                logical = view.I64(48),
                initialized = view.I64(56);
            long? physical = minimum == 72 ? view.I64(64) : null;
            if (
                allocated < 0
                || logical < 0
                || initialized < 0
                || initialized > logical
                || physical is < 0
                || physical > allocated
            )
                throw view.Error("Invalid stream sizes", 40);
            sizes = new(allocated, logical, initialized, physical);
        }
        var runs = DataRunParser.Parse(
            view.Slice(pairsOffset, bytes.Length - pairsOffset),
            lowest,
            highest,
            origin + pairsOffset
        );
        return new NonResidentAttribute(
            type,
            id,
            name,
            flags,
            lowest,
            highest,
            compressionUnit,
            sizes,
            runs
        )
        {
            ReservedHeaderBytes = ImmutableArray.Create(view.Slice(35, 5).ToArray()),
        };
    }

    private static void ValidateKnownValue(uint type, ReadOnlySpan<byte> value, int origin)
    {
        try
        {
            if (type == NtfsAttributeTypes.StandardInformation)
                AttributeValueParser.ParseStandardFileAttributes(value);
            if (type == NtfsAttributeTypes.FileName)
                AttributeValueParser.ParseFileName(value);
            if (type == NtfsAttributeTypes.AttributeList)
                AttributeValueParser.ParseAttributeList(value);
        }
        catch (NtfsParseException error)
        {
            throw new NtfsParseException(
                "Invalid attribute value: " + error.Message,
                origin + error.Offset
            );
        }
    }
}
