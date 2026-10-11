namespace Something.Ntfs;

public enum NtfsMetadataKind
{
    Unclassified,
    BadClusters,
}

/// <summary>
/// Classifies parsed metadata for explicit capacity policies outside the record parser.
/// Classification is not proof that the file is uncorrupted or that its capacity is zero.
/// </summary>
public static class NtfsMetadataClassifier
{
    public static NtfsMetadataKind Classify(MftRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (
            !record.IsInUse
            || record.IsDirectory
            || record.IsExtension
            || record.Reference.RecordIndex != 8
        )
            return NtfsMetadataKind.Unclassified;
        bool hasBadStream = record
            .Attributes.OfType<NonResidentAttribute>()
            .Any(a =>
                a.Type == NtfsAttributeTypes.Data
                && a.LowestVcn == 0
                && string.Equals(a.Name, "$Bad", StringComparison.OrdinalIgnoreCase)
            );
        if (!hasBadStream)
            return NtfsMetadataKind.Unclassified;
        bool hasReservedName = record
            .Attributes.OfType<ResidentAttribute>()
            .Where(a => a.Type == NtfsAttributeTypes.FileName)
            .Select(a => AttributeValueParser.ParseFileName(a.Value.AsSpan()))
            .Any(n =>
                n.Parent.RecordIndex == 5
                && n.Namespace is FileNameNamespace.Win32 or FileNameNamespace.Win32AndDos
                && string.Equals(n.Name, "$BadClus", StringComparison.OrdinalIgnoreCase)
            );
        return hasReservedName ? NtfsMetadataKind.BadClusters : NtfsMetadataKind.Unclassified;
    }
}
