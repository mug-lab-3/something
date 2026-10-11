namespace Something.Ntfs;

/// <summary>A malformed on-disk structure. Offset is relative to the supplied structure.</summary>
public sealed class NtfsParseException(string message, int offset) : Exception($"{message} (offset 0x{offset:X}).")
{
    public int Offset { get; } = offset;
}
