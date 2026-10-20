using System.Text.Json;
using Something.Statistics;

namespace Something.Application;

/// <summary>Small user preferences, separate from scan databases.</summary>
public sealed class DisplaySettingsStore(string path)
{
    /// <summary>The per-user settings file of the installed application.</summary>
    public static string DefaultPath { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Something",
            "display-settings.json"
        );

    public FileVisibilityOptions Load()
    {
        var settings = Read();
        return new(settings.IncludeSystem, settings.IncludeHidden);
    }

    public ThemePreference LoadTheme()
    {
        var theme = Read().Theme;
        return Enum.IsDefined(theme) ? theme : ThemePreference.System;
    }

    /// <summary>Whether moving between folders in the list reveals the folder in the tree.</summary>
    public bool LoadFollowListInTree() => Read().FollowListInTree;

    public void SaveFollowListInTree(bool follow) =>
        Write(Read() with { FollowListInTree = follow });

    public LanguagePreference LoadLanguage()
    {
        var language = Read().Language;
        return Enum.IsDefined(language) ? language : LanguagePreference.System;
    }

    public void SaveLanguage(LanguagePreference language)
    {
        if (!Enum.IsDefined(language))
            throw new ArgumentOutOfRangeException(nameof(language));
        Write(Read() with { Language = language });
    }

    /// <summary>Whether the application asks GitHub for a newer release at startup.</summary>
    public bool LoadCheckForUpdates() => Read().CheckForUpdates;

    public void SaveCheckForUpdates(bool check) => Write(Read() with { CheckForUpdates = check });

    private Preferences Read()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? new()
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

    public void Save(FileVisibilityOptions options) =>
        Write(
            Read() with
            {
                IncludeSystem = options.IncludeSystem,
                IncludeHidden = options.IncludeHidden,
            }
        );

    public void SaveTheme(ThemePreference theme)
    {
        if (!Enum.IsDefined(theme))
            throw new ArgumentOutOfRangeException(nameof(theme));
        Write(Read() with { Theme = theme });
    }

    private void Write(Preferences settings)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private sealed record Preferences(
        bool IncludeSystem = true,
        bool IncludeHidden = true,
        ThemePreference Theme = ThemePreference.System,
        bool FollowListInTree = true,
        bool CheckForUpdates = true,
        LanguagePreference Language = LanguagePreference.System
    );
}
