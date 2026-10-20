using Something.Ntfs;

namespace Something.Windows;

public sealed record FileRecycleTarget(
    string Drive,
    string Path,
    string VolumeKey,
    ulong VolumeSerial,
    FileReference Reference,
    bool IsDirectory
)
{
    public uint? StandardFileAttributes { get; init; }
    public bool HiddenDeletionConfirmed { get; init; }
}

public enum FileRecycleResult
{
    Ready,
    Protected,
    HiddenNeedsConfirmation,
    Missing,
    Replaced,
    Cancelled,
    Recycled,
}

public interface IFileRecycleBackend
{
    Task<FileRecycleResult> InspectAsync(FileRecycleTarget target, CancellationToken token);
    Task<FileRecycleResult> RecycleAsync(
        FileRecycleTarget target,
        nint owner,
        CancellationToken token
    );
}

/// <summary>Reject Win32 normalization aliases, device paths, streams and directory traversal.
/// A scanned name never becomes a command or a wildcard operation.</summary>
public static class RecyclePath
{
    public static void Validate(FileRecycleTarget target)
    {
        string path = target.Path;
        if (
            target.Drive.Length != 2
            || !char.IsAsciiLetter(target.Drive[0])
            || target.Drive[1] != ':'
            || path.Length <= 3
            || path[2] != '\\'
            || !path.StartsWith(target.Drive, StringComparison.OrdinalIgnoreCase)
            || target.Reference.RecordIndex < 16
        )
            throw new ArgumentException(
                "The recycle target path cannot be verified safely."
            ).WithFailure(RecycleFailure.UnsafePath);
        foreach (string segment in path[3..].Split('\\'))
        {
            if (
                segment.Length == 0
                || segment is "." or ".."
                || segment.EndsWith(' ')
                || segment.EndsWith('.')
                || segment.Any(c => c < 32 || "<>:\"/|?*".Contains(c))
            )
                throw new ArgumentException(
                    "A recycle target name cannot be verified safely."
                ).WithFailure(RecycleFailure.UnsafeName);
            string stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (
                stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || stem.Length == 4
                    && (
                        stem.StartsWith("COM", StringComparison.Ordinal)
                        || stem.StartsWith("LPT", StringComparison.Ordinal)
                    )
                    && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')
            )
                throw new ArgumentException("Device names cannot be recycled.").WithFailure(
                    RecycleFailure.DeviceName
                );
        }
    }
}
