using System.Diagnostics;
using System.Globalization;
using Microsoft.Windows.Globalization;
using Something.Application;

namespace Something.App;

/// <summary>
/// The display language for this process. XAML resolves x:Uid text when each element loads, so
/// the language is chosen once before the UI starts; a different choice applies after a restart.
/// </summary>
internal static class AppLanguage
{
    private static IReadOnlyList<string>? restartArguments;

    /// <summary>The BCP 47 tag of the displayed text, such as "ja-JP".</summary>
    internal static string Current { get; private set; } = DisplayLanguage.Japanese;

    /// <summary>The preference this process started with.</summary>
    internal static LanguagePreference Preference { get; private set; }

    /// <summary>The system's display language, captured before the override is applied.</summary>
    internal static CultureInfo SystemCulture { get; } = CultureInfo.CurrentUICulture;

    /// <summary>Applies the language before any resource is loaded.</summary>
    internal static void Apply(string[] arguments)
    {
        // A demo keeps no settings, so its language comes from the command line.
        Preference = arguments.Contains("--demo")
            ? DisplayLanguage.FromArguments(arguments) ?? LanguagePreference.System
            : new DisplaySettingsStore(DisplaySettingsStore.DefaultPath).LoadLanguage();
        Current = DisplayLanguage.Resolve(Preference, SystemCulture);
        ApplicationLanguages.PrimaryLanguageOverride = Current;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture =
            CultureInfo.GetCultureInfo(Current);
    }

    /// <summary>Whether the preference shows a different language than this process.</summary>
    internal static bool NeedsRestart(LanguagePreference preference) =>
        DisplayLanguage.Resolve(preference, SystemCulture) != Current;

    /// <summary>Starts the application again after this process has shut down.</summary>
    internal static void RequestRestart(bool demo, LanguagePreference preference) =>
        restartArguments = DisplayLanguage.RestartArguments(
            Environment.GetCommandLineArgs()[1..],
            demo,
            preference
        );

    /// <summary>Called once the single-instance registration is released.</summary>
    internal static void RestartIfRequested()
    {
        if (restartArguments is not { } arguments || Environment.ProcessPath is not { } path)
            return;
        // The new process inherits this process's elevation, so no second UAC prompt appears.
        var start = new ProcessStartInfo(path) { UseShellExecute = false };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
        {
            // The window is already closed; the user can start the application again.
            Debug.WriteLine($"Restart failed: {error}");
        }
    }
}
