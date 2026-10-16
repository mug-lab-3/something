using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Something.Application;
using Something.Statistics;

namespace Something.App;

public sealed partial class MainWindow
{
    private FileVisibilityOptions visibilityOptions = new();
    private DisplaySettingsStore? displaySettings;
    private bool updatingSettings;
    private bool changingVisibility;

    private void InitializeDisplaySettings()
    {
        if (!demo)
        {
            displaySettings = new(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Something",
                    "display-settings.json"
                )
            );
            visibilityOptions = displaySettings.Load();
        }
        UpdateSettingsChecks();
    }

    private void UpdateSettingsChecks()
    {
        updatingSettings = true;
        IncludeSystemItem.IsChecked = visibilityOptions.IncludeSystem;
        IncludeHiddenItem.IsChecked = visibilityOptions.IncludeHidden;
        updatingSettings = false;
    }

    private async void OnVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (updatingSettings || closed)
            return;
        if (historyMaintenance || viewUpdates.Count != 0)
        {
            UpdateSettingsChecks();
            return;
        }
        var options = new FileVisibilityOptions(
            IncludeSystemItem.IsChecked,
            IncludeHiddenItem.IsChecked
        );
        var task = ChangeVisibilityAsync(options);
        historyTasks.Add(task);
        try
        {
            await task;
        }
        finally
        {
            historyTasks.Remove(task);
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
            await using var update = BeginViewUpdate("表示するファイルと容量を集計しています…");
            visibilityOptions = options;
            UpdateSettingsChecks();
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
            UpdateSettingsChecks();
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
            IncludeSystemItem.IsChecked
            || IncludeHiddenItem.IsChecked
            || current.RootSample != total
            || Entries.Items.Count != 3
            || Ranking.Items.Count != 3
            || HasPersistentNotification
        )
            throw new InvalidOperationException(
                "Display settings did not apply consistently to unknown attributes."
            );
        await SelectDriveAsync(cards[1]);
        await SelectDriveAsync(cards[0]);
        if (IncludeSystemItem.IsChecked || IncludeHiddenItem.IsChecked || Entries.Items.Count != 3)
            throw new InvalidOperationException("Drive navigation lost the display settings.");
        await ChangeVisibilityAsync(new());
        if (
            !IncludeSystemItem.IsChecked
            || !IncludeHiddenItem.IsChecked
            || current.RootSample != total
        )
            throw new InvalidOperationException("Display settings did not restore full results.");
    }
}
