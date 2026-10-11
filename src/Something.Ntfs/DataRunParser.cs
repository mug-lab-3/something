using System.Collections.Immutable;

namespace Something.Ntfs;

/// <summary>Decodes the signed relative LCNs in a nonresident attribute's mapping pairs.</summary>
public static class DataRunParser
{
    public static ImmutableArray<DataRun> Parse(
        ReadOnlySpan<byte> mappingPairs,
        long lowestVcn,
        long highestVcn
    ) => Parse(mappingPairs, lowestVcn, highestVcn, 0);

    internal static ImmutableArray<DataRun> Parse(
        ReadOnlySpan<byte> mappingPairs,
        long lowestVcn,
        long highestVcn,
        int origin
    )
    {
        var view = new BinaryView(mappingPairs, origin);
        if (
            lowestVcn < 0
            || highestVcn < -1
            || highestVcn == long.MaxValue
            || (highestVcn < lowestVcn && !(lowestVcn == 0 && highestVcn == -1))
        )
            throw view.Error("Invalid VCN range", 0);

        var runs = ImmutableArray.CreateBuilder<DataRun>();
        long vcn = lowestVcn,
            lcn = 0;
        int offset = 0;
        while (true)
        {
            byte header = view.U8(offset++);
            if (header == 0)
                break;
            int lengthBytes = header & 15,
                deltaBytes = header >> 4;
            if (lengthBytes is < 1 or > 8 || deltaBytes > 8)
                throw view.Error("Invalid mapping-pair field width", offset - 1);
            view.Require(offset, lengthBytes + deltaBytes);
            ulong count = Unsigned(view.Slice(offset, lengthBytes));
            offset += lengthBytes;
            if (count == 0 || count > long.MaxValue || (long)count > long.MaxValue - vcn)
                throw view.Error("Invalid or overflowing run length", offset - lengthBytes);
            long? physical = null;
            if (deltaBytes != 0)
            {
                long delta = Signed(view.Slice(offset, deltaBytes));
                try
                {
                    lcn = checked(lcn + delta);
                }
                catch (OverflowException)
                {
                    throw view.Error("LCN overflow", offset);
                }
                if (lcn < 0 || lcn > long.MaxValue - (long)count)
                    throw view.Error("Invalid physical cluster range", offset);
                physical = lcn;
            }
            offset += deltaBytes;
            runs.Add(new(vcn, (long)count, physical));
            vcn += (long)count;
            if (vcn > highestVcn + 1)
                throw view.Error("Run exceeds declared VCN range", offset);
        }
        if (vcn != highestVcn + 1)
            throw view.Error("Runs do not cover declared VCN range", offset);
        return runs.ToImmutable();
    }

    private static ulong Unsigned(ReadOnlySpan<byte> bytes)
    {
        ulong result = 0;
        for (int i = 0; i < bytes.Length; i++)
            result |= (ulong)bytes[i] << (8 * i);
        return result;
    }

    private static long Signed(ReadOnlySpan<byte> bytes)
    {
        ulong value = Unsigned(bytes);
        if (bytes.Length < 8 && (bytes[^1] & 0x80) != 0)
            value |= ulong.MaxValue << (bytes.Length * 8);
        return unchecked((long)value);
    }
}
