namespace Something.Statistics;

/// <summary>Read-time policy only. Unknown attributes remain visible, never assumed normal.</summary>
public sealed record FileVisibilityOptions(bool IncludeSystem = true, bool IncludeHidden = true)
{
    public bool IncludesAll => IncludeSystem && IncludeHidden;

    public bool Includes(uint? attributes) =>
        attributes is not { } value
        || (
            (IncludeSystem || !((FileAttributes)value).HasFlag(FileAttributes.System))
            && (IncludeHidden || !((FileAttributes)value).HasFlag(FileAttributes.Hidden))
        );
}
