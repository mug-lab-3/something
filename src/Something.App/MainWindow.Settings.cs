using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Something.Application;
using Something.Statistics;

namespace Something.App;

public sealed partial class MainWindow
{
    private FileVisibilityOptions visibilityOptions = new();
    private DisplaySettingsStore? displaySettings;
    private ThemePreference themePreference;
    private bool followListInTree = true;
    private LanguagePreference languagePreference = AppLanguage.Preference;

    private bool changingVisibility;

    private void InitializeDisplaySettings()
    {
        if (!demo)
        {
            displaySettings = new(DisplaySettingsStore.DefaultPath);
            visibilityOptions = displaySettings.Load();
            themePreference = displaySettings.LoadTheme();
            followListInTree = displaySettings.LoadFollowListInTree();
            checkForUpdates = displaySettings.LoadCheckForUpdates();
        }
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        WindowLayout.RequestedTheme = themePreference switch
        {
            ThemePreference.Dark => ElementTheme.Dark,
            ThemePreference.Light => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
        AppWindow.TitleBar.PreferredTheme = themePreference switch
        {
            ThemePreference.Dark => TitleBarTheme.Dark,
            ThemePreference.Light => TitleBarTheme.Light,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    private void ChangeTheme(ThemePreference theme)
    {
        try
        {
            displaySettings?.SaveTheme(theme);
            themePreference = theme;
            ApplyTheme();
        }
        catch (Exception error)
        {
            SettingsPage.SetTheme(themePreference);
            ShowError(error);
        }
    }

    // Text is resolved when the window loads, so a new language applies after a restart.
    private void ChangeLanguage(LanguagePreference language)
    {
        try
        {
            displaySettings?.SaveLanguage(language);
            languagePreference = language;
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        SettingsPage.SetLanguage(languagePreference, AppLanguage.NeedsRestart(languagePreference));
    }

    private async Task RestartForLanguageAsync()
    {
        if (closed || !CanManageDatabase)
            return;
        AppLanguage.RequestRestart(demo, languagePreference);
        try
        {
            await ShutdownAsync();
        }
        finally
        {
            allowClose = true;
            Close();
        }
    }

    private void ChangeFollowList(bool follow)
    {
        try
        {
            displaySettings?.SaveFollowListInTree(follow);
            followListInTree = follow;
            // Show the current folder right away when following is turned on.
            if (follow && location is { } current)
                RevealCurrentFolder(current);
        }
        catch (Exception error)
        {
            SettingsPage.SetFollowList(followListInTree);
            ShowError(error);
        }
    }

    private async Task ChangeVisibilityAsync(FileVisibilityOptions options)
    {
        var previous = visibilityOptions;
        if (previous == options)
            return;
        changingVisibility = true;
        SettingsButton.IsEnabled = false;
        WorkspaceTabs.IsEnabled = false;
        foreach (var card in cards)
            card.IsEnabled = false;
        try
        {
            await using var update = BeginViewUpdate(Strings.Get("Settings_Applying"));
            visibilityOptions = options;

            if (session is { } current)
            {
                await current.SetVisibilityAsync(options, lifetime.Token);
                if (session == current)
                {
                    if (Comparing)
                        await RefreshComparisonAsync();
                    else
                        await ShowSessionAsync(current);
                }
            }
            displaySettings?.Save(options);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            visibilityOptions = previous;

            // Restore both the read model and the checkboxes if applying/saving fails.
            try
            {
                if (session is { } current)
                {
                    await current.SetVisibilityAsync(previous, lifetime.Token);
                    if (Comparing)
                        await RefreshComparisonAsync();
                    else
                        await ShowSessionAsync(current);
                }
            }
            catch (Exception restoreError)
            {
                error = new AggregateException(error, restoreError);
            }
            ShowError(error);
        }
        finally
        {
            changingVisibility = false;
            if (!closed)
            {
                SettingsButton.IsEnabled = !historyMaintenance && viewUpdates.Count == 0;
                UpdateSettingsAvailability();
                WorkspaceTabs.IsEnabled = !historyMaintenance;
                foreach (var card in cards)
                    card.IsEnabled = !historyMaintenance;
            }
        }
    }

    private async Task VerifyDisplaySettingsAsync()
    {
        if (session is not { } current)
            throw new InvalidOperationException("Settings smoke needs a scan.");
        var total = current.RootSample;
        // Demo attributes are unknown: excluding known flags must preserve its contents.
        await ChangeVisibilityAsync(new(false, false));
        if (
            visibilityOptions.IncludeSystem
            || visibilityOptions.IncludeHidden
            || current.RootSample != total
            || Entries.Items.Count != 3
            || Ranking.Items.Count != 3
            || HasPersistentNotification
        )
            throw new InvalidOperationException(
                "Display settings did not apply consistently to unknown attributes."
            );
        var filteredRoot = current.RootSample;
        var filteredPage = await current.ReadFolderAsync(current.Root, 0, false, lifetime.Token);
        var filteredAnalysis = await current.ReadFolderAnalysisAsync(new(), lifetime.Token);
        await SelectDriveAsync(cards[1]);
        await SelectDriveAsync(cards[0]);
        if (
            session != current
            || !ReferenceEquals(current.RootSample, filteredRoot)
            || !ReferenceEquals(
                filteredPage,
                await current.ReadFolderAsync(current.Root, 0, false, lifetime.Token)
            )
            || !ReferenceEquals(
                filteredAnalysis[0],
                (await current.ReadFolderAnalysisAsync(new(), lifetime.Token))[0]
            )
        )
            throw new InvalidOperationException(
                "Drive navigation did not reuse its cached projection, root listing and analysis."
            );
        if (
            visibilityOptions.IncludeSystem
            || visibilityOptions.IncludeHidden
            || Entries.Items.Count != 3
        )
            throw new InvalidOperationException("Drive navigation lost the display settings.");
        await ChangeVisibilityAsync(new());
        await ChangeVisibilityAsync(new(false, false));
        if (!ReferenceEquals(current.RootSample, filteredRoot))
            throw new InvalidOperationException(
                "Returning to a setting recalculated its projection."
            );
        await ChangeVisibilityAsync(new());
        if (
            !visibilityOptions.IncludeSystem
            || !visibilityOptions.IncludeHidden
            || current.RootSample != total
        )
            throw new InvalidOperationException("Display settings did not restore full results.");
    }
}
