using Something.Application;

namespace Something.Ntfs.Tests;

public sealed class ThemeSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"something-theme-{Guid.NewGuid():N}"
    );
    private string SettingsPath => Path.Combine(directory, "display-settings.json");

    [Theory]
    [InlineData(ThemePreference.System)]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.Light)]
    public void ThemeSurvivesReloadAndVisibilityChanges(ThemePreference theme)
    {
        var store = new DisplaySettingsStore(SettingsPath);
        store.Save(new(false, true));

        store.SaveTheme(theme);
        var reloaded = new DisplaySettingsStore(SettingsPath);

        Assert.Equal(theme, reloaded.LoadTheme());
        Assert.Equal(new(false, true), reloaded.Load());
        reloaded.Save(new(true, false));
        Assert.Equal(theme, new DisplaySettingsStore(SettingsPath).LoadTheme());
        Assert.Single(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{\"IncludeSystem\":false}")]
    [InlineData("{\"Theme\":999}")]
    [InlineData("{\"Theme\":\"Unknown\"}")]
    public void MissingOrInvalidThemeUsesSystem(string? json)
    {
        if (json is not null)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, json);
        }

        Assert.Equal(ThemePreference.System, new DisplaySettingsStore(SettingsPath).LoadTheme());
    }

    [Fact]
    public void InvalidThemeCannotOverwritePreferences()
    {
        var store = new DisplaySettingsStore(SettingsPath);
        store.SaveTheme(ThemePreference.Dark);

        Assert.Throws<ArgumentOutOfRangeException>(() => store.SaveTheme((ThemePreference)999));
        Assert.Equal(ThemePreference.Dark, store.LoadTheme());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
