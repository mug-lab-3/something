using System.Globalization;
using Something.Application;

namespace Something.Ntfs.Tests;

public sealed class DisplayLanguageTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"something-display-language-{Guid.NewGuid():N}"
    );
    private string SettingsPath => Path.Combine(directory, "display-settings.json");

    [Theory]
    [InlineData("ja-JP", "ja-JP")]
    [InlineData("ja", "ja-JP")]
    [InlineData("en-US", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("fr-FR", "en-US")]
    [InlineData("zh-CN", "en-US")]
    [InlineData("", "en-US")]
    public void Resolve_FollowsSystemJapaneseOrElseEnglish(string system, string expected) =>
        Assert.Equal(
            expected,
            DisplayLanguage.Resolve(LanguagePreference.System, CultureInfo.GetCultureInfo(system))
        );

    [Theory]
    [InlineData(LanguagePreference.Japanese, "en-US", "ja-JP")]
    [InlineData(LanguagePreference.English, "ja-JP", "en-US")]
    public void Resolve_UsesAnExplicitChoiceRegardlessOfTheSystem(
        LanguagePreference preference,
        string system,
        string expected
    ) =>
        Assert.Equal(
            expected,
            DisplayLanguage.Resolve(preference, CultureInfo.GetCultureInfo(system))
        );

    [Fact]
    public void Resolve_RejectsUndefinedPreferences() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DisplayLanguage.Resolve((LanguagePreference)99, CultureInfo.InvariantCulture)
        );

    [Theory]
    [InlineData(new[] { "--demo" }, null)]
    [InlineData(new[] { "--demo", "--language", "ja-JP" }, LanguagePreference.Japanese)]
    [InlineData(new[] { "--language", "en-US", "--demo" }, LanguagePreference.English)]
    [InlineData(new[] { "--demo", "--language", "system" }, LanguagePreference.System)]
    public void FromArguments_ReadsTheLanguageOption(
        string[] arguments,
        LanguagePreference? expected
    ) => Assert.Equal(expected, DisplayLanguage.FromArguments(arguments));

    [Theory]
    [InlineData("--demo --language")]
    [InlineData("--demo --language fr-FR")]
    [InlineData("--demo --language EN-us")]
    public void FromArguments_RejectsMissingOrUnsupportedValues(string arguments) =>
        Assert.Throws<ArgumentException>(() => DisplayLanguage.FromArguments(Split(arguments)));

    [Fact]
    public void RestartArguments_CarryTheChoiceOnTheDemoCommandLine()
    {
        // Arrange
        string[] arguments = ["--demo", "--language", "en-US", "--demo-history", "h"];

        // Act
        var restart = DisplayLanguage.RestartArguments(
            arguments,
            demo: true,
            LanguagePreference.Japanese
        );

        // Assert
        Assert.Equal(["--demo", "--demo-history", "h", "--language", "ja-JP"], restart);
        Assert.Equal(LanguagePreference.Japanese, DisplayLanguage.FromArguments(restart));
    }

    [Theory]
    [InlineData("")]
    [InlineData("--language en-US")]
    [InlineData("--language en-US --language")]
    public void RestartArguments_LeaveANormalStartToTheSavedSettings(string arguments) =>
        Assert.Empty(
            DisplayLanguage.RestartArguments(
                Split(arguments),
                demo: false,
                LanguagePreference.English
            )
        );

    [Theory]
    [InlineData(null)]
    [InlineData("{broken")]
    [InlineData("{\"Theme\":\"Dark\",\"CheckForUpdates\":false}")]
    [InlineData("{\"Language\":99}")]
    public void Language_DefaultsToSystemForMissingOlderOrInvalidSettings(string? json)
    {
        // Arrange
        if (json is not null)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, json);
        }

        // Act
        var language = new DisplaySettingsStore(SettingsPath).LoadLanguage();

        // Assert
        Assert.Equal(LanguagePreference.System, language);
    }

    [Fact]
    public void Language_SurvivesReloadWithoutChangingOtherPreferences()
    {
        // Arrange
        var store = new DisplaySettingsStore(SettingsPath);
        store.SaveTheme(ThemePreference.Dark);
        store.SaveCheckForUpdates(false);

        // Act
        store.SaveLanguage(LanguagePreference.English);
        var reloaded = new DisplaySettingsStore(SettingsPath);

        // Assert
        Assert.Equal(LanguagePreference.English, reloaded.LoadLanguage());
        Assert.Equal(ThemePreference.Dark, reloaded.LoadTheme());
        Assert.False(reloaded.LoadCheckForUpdates());
        Assert.Contains("\"English\"", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Language_RejectsUndefinedPreferences() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DisplaySettingsStore(SettingsPath).SaveLanguage((LanguagePreference)99)
        );

    private static string[] Split(string arguments) =>
        arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
