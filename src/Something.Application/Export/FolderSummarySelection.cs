using System.Numerics;

namespace Something.Application.Export;

/// <summary>Limits for a folder summary; null means no limit.</summary>
public sealed record FolderSummaryLimits(
    int? MaximumDepth = null,
    UInt128 MinimumBytes = default,
    int? Limit = null,
    bool SortByChange = false
);

/// <summary>Selects the folders of a summary below a start folder.</summary>
public static class FolderSummarySelection
{
    /// <summary>
    /// Depth of a drive-relative folder path below the start folder ("" is the root), or null when
    /// the folder is not the start folder or one of its descendants.
    /// </summary>
    public static int? Depth(string path, string start)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(start);
        if (start.Length == 0)
            return path.Length == 0 ? 0 : path.Count(c => c == '\\') + 1;
        if (path == start)
            return 0;
        // Require the separator so that "A\B2" is not treated as a child of "A\B".
        return path.StartsWith(start + "\\", StringComparison.Ordinal)
            ? path.Count(c => c == '\\') - start.Count(c => c == '\\')
            : null;
    }

    /// <summary>
    /// Keeps folders inside the start folder within the limits, largest first (or largest change
    /// first). Unknown sizes never meet a positive minimum. Ties keep the input order.
    /// </summary>
    public static (T Folder, int Depth)[] Select<T>(
        IEnumerable<T> folders,
        string start,
        FolderSummaryLimits limits,
        Func<T, string> path,
        Func<T, UInt128?> size,
        Func<T, BigInteger?> delta
    )
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "The depth limit is negative.");
        if (limits.Limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limits), "The row limit is below one.");
        var rows = folders
            .Select(folder => (Folder: folder, Depth: Depth(path(folder), start)))
            .Where(row =>
                row.Depth is { } depth
                && (limits.MaximumDepth is not { } maximum || depth <= maximum)
                && (
                    limits.MinimumBytes == UInt128.Zero
                    || size(row.Folder) is { } bytes && bytes >= limits.MinimumBytes
                )
            )
            .Select(row => (row.Folder, Depth: row.Depth!.Value));
        var ordered = limits.SortByChange
            ? rows.OrderByDescending(row => BigInteger.Abs(delta(row.Folder) ?? BigInteger.Zero))
            : rows.OrderByDescending(row => size(row.Folder) ?? UInt128.Zero);
        return [.. limits.Limit is { } limit ? ordered.Take(limit) : ordered];
    }
}
