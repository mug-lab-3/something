namespace Something.Windows;

/// <summary>Protection applies to a target, independently of historical display filters.</summary>
public static class FileRecyclePolicy
{
    private static readonly string[] StandardRootFolders =
    [
        "Windows",
        "Program Files",
        "Program Files (x86)",
        "ProgramData",
        "Users",
        "Documents and Settings",
        "System Volume Information",
        "$Recycle.Bin",
        "Recovery",
        "PerfLogs",
    ];

    private static readonly string[] KnownFolders = Enum.GetValues<Environment.SpecialFolder>()
        .Select(folder => Environment.GetFolderPath(folder))
        .Where(path => path.Length > 3)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static readonly string[] ProfileFolders =
    [
        "Desktop",
        "Documents",
        "Downloads",
        "Pictures",
        "Music",
        "Videos",
        "Favorites",
        "Contacts",
        "Links",
        "Saved Games",
        "Searches",
        "AppData",
        "AppData\\Local",
        "AppData\\LocalLow",
        "AppData\\Roaming",
    ];

    public static bool IsProtected(FileRecycleTarget target)
    {
        if ((target.StandardFileAttributes.GetValueOrDefault() & (uint)FileAttributes.System) != 0)
            return true;
        string path = target.Path.TrimEnd('\\');
        string windows = target.Drive + "\\Windows";
        string actualWindows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (Within(path, windows) || actualWindows.Length > 3 && Within(path, actualWindows))
            return true;
        if (!target.IsDirectory)
            return false;
        if (
            StandardRootFolders.Any(name =>
                path.Equals(target.Drive + "\\" + name, StringComparison.OrdinalIgnoreCase)
            )
        )
            return true;
        if (KnownFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
            return true;
        // Protect profile containers, including profiles belonging to other Windows users.
        string users = target.Drive + "\\Users\\";
        if (!path.StartsWith(users, StringComparison.OrdinalIgnoreCase))
            return false;
        string relative = path[users.Length..];
        int separator = relative.IndexOf('\\');
        return separator < 0
            || ProfileFolders.Contains(
                relative[(separator + 1)..],
                StringComparer.OrdinalIgnoreCase
            );
    }

    public static bool IsHidden(FileRecycleTarget target) =>
        (target.StandardFileAttributes.GetValueOrDefault() & (uint)FileAttributes.Hidden) != 0;

    private static bool Within(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
}
