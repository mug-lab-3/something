using Microsoft.UI.Xaml.Controls;
using Something.Application;

namespace Something.App;

public sealed partial class MainWindow
{
    // The display language and the restart that applies a new choice. Start with
    // --language en-US: the English run checks its text, chooses Japanese in the settings and
    // restarts; the restarted run checks the Japanese text with the same history and stops.
    private async Task VerifyLanguageAsync(string capture)
    {
        if (!demo)
            throw new InvalidOperationException("Language smoke needs the demo.");
        bool english = AppLanguage.Current == DisplayLanguage.English;
        // Code text and x:Uid text both follow the language.
        (string toast, string empty) = english
            ? ("Done", "Start with the scan button at the top right of a drive.")
            : ("完了しました", "ドライブの右上にあるスキャンボタンから開始してください。");
        if (Strings.Get("Toast_Success") != toast || EmptyScanText.Text != empty)
            throw new InvalidOperationException(
                $"The {AppLanguage.Current} text was not loaded: {EmptyScanText.Text}"
            );

        var box = (ComboBox)SettingsPage.FindName("LanguageBox");
        var restartBar = (InfoBar)SettingsPage.FindName("LanguageRestartBar");
        var restart = (Button)SettingsPage.FindName("RestartButton");
        SetSettingsOpen(true);
        if (box.SelectedIndex != (int)AppLanguage.Preference || restartBar.IsOpen)
            throw new InvalidOperationException("The settings did not show the running language.");
        await Task.Delay(500, lifetime.Token);
        await CaptureStartupFrameAsync($"{capture}.language-{AppLanguage.Current}");
        if (!english)
        {
            SetSettingsOpen(false);
            await File.WriteAllTextAsync(
                capture + ".language.result",
                "English text, the settings choice, the restart notice, the restart and Japanese text with the same history passed.",
                lifetime.Token
            );
            return;
        }

        // Choosing another language asks for a restart; choosing the running one again does not.
        box.SelectedIndex = (int)LanguagePreference.Japanese;
        if (!restartBar.IsOpen || !restart.IsEnabled)
            throw new InvalidOperationException("Choosing Japanese did not offer a restart.");
        await Task.Delay(500, lifetime.Token);
        await CaptureStartupFrameAsync(capture + ".language-restart");
        box.SelectedIndex = (int)LanguagePreference.English;
        if (restartBar.IsOpen)
            throw new InvalidOperationException("The running language still asked for a restart.");
        box.SelectedIndex = (int)LanguagePreference.Japanese;
        SetSettingsOpen(false);
        // The smoke run shuts down normally afterwards, which starts the Japanese run.
        AppLanguage.RequestRestart(demo, languagePreference);
    }
}
