using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Something.Application;

namespace Something.App;

public sealed partial class MainWindow
{
    private async Task VerifyThemeSettingsAsync(string capture)
    {
        if (!demo || displaySettings is not null)
            throw new InvalidOperationException(
                "Theme preview must not write real user preferences."
            );
        var original = themePreference;
        SetSettingsOpen(true);
        try
        {
            var choices = (ComboBox)SettingsPage.FindName("ThemeBox");
            foreach (
                var theme in new[]
                {
                    ThemePreference.Dark,
                    ThemePreference.Light,
                    ThemePreference.System,
                }
            )
            {
                choices.SelectedIndex = (int)theme;
                await Task.Delay(150, lifetime.Token);
                var expected = theme switch
                {
                    ThemePreference.Dark => ElementTheme.Dark,
                    ThemePreference.Light => ElementTheme.Light,
                    _ => ElementTheme.Default,
                };
                if (
                    themePreference != theme
                    || WindowLayout.RequestedTheme != expected
                    || (
                        expected != ElementTheme.Default
                        && (
                            SettingsPage.ActualTheme != expected
                            || RootLayout.ActualTheme != expected
                        )
                    )
                )
                    throw new InvalidOperationException(
                        $"Theme selection did not apply throughout the window: {theme}."
                    );
                await CaptureStartupFrameAsync(capture + ".theme-" + theme);
            }
        }
        finally
        {
            ChangeTheme(original);
            SettingsPage.SetTheme(original);
            SetSettingsOpen(false);
        }
    }
}
