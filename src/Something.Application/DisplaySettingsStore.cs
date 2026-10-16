using System.Text.Json;
using Something.Statistics;

namespace Something.Application;

/// <summary>Small user preferences, separate from scan databases.</summary>
public sealed class DisplaySettingsStore(string path)
{
    public FileVisibilityOptions Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<FileVisibilityOptions>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
        catch (IOException)
        {
            return new();
        }
        catch (UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(FileVisibilityOptions options)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(options));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
