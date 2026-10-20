namespace Something.Application;

/// <summary>Display paths from a tree root down to a folder, used to reveal the folder in the tree.</summary>
public static class FolderPathSteps
{
    /// <summary>
    /// The root path followed by the path of each folder down to <paramref name="target"/>, or null
    /// when the target is not the root or below it. Paths use "\" and the root keeps its trailing
    /// separator ("C:\"); names are compared ordinally because NTFS names can differ only in case.
    /// </summary>
    public static string[]? From(string root, string target)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(target);
        string prefix = root.TrimEnd('\\');
        string path = target.TrimEnd('\\');
        if (path == prefix)
            return [root];
        if (prefix.Length == 0 || !path.StartsWith(prefix + "\\", StringComparison.Ordinal))
            return null;
        string[] names = path[(prefix.Length + 1)..].Split('\\');
        if (names.Any(name => name.Length == 0))
            return null;
        var steps = new string[names.Length + 1];
        steps[0] = root;
        string current = prefix;
        for (int index = 0; index < names.Length; index++)
        {
            current += "\\" + names[index];
            steps[index + 1] = current;
        }
        return steps;
    }
}
