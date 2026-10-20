using System.Text.Json;
using Something.Storage;

namespace Something.Application;

public sealed class DatabaseSettingsStore(string path)
{
    public string? LoadDirectory()
    {
        try
        {
            if (!File.Exists(path))
                return null;
            string? directory = JsonSerializer
                .Deserialize<DatabaseLocation>(File.ReadAllText(path))
                ?.Directory;
            return directory is not null && Path.IsPathFullyQualified(directory)
                ? Path.GetFullPath(directory)
                : null;
        }
        catch (Exception error)
            when (error
                    is JsonException
                        or IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or NotSupportedException
            )
        {
            return null;
        }
    }

    public void SaveDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException(
                "The database location must be an absolute path.",
                nameof(directory)
            ).WithFailure(HistoryFailure.RelativeLocation);
        string parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, $".database-settings-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(new DatabaseLocation(Path.GetFullPath(directory)))
            );
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private sealed record DatabaseLocation(string Directory);
}
