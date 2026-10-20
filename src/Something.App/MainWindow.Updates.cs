using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Something.Application;

namespace Something.App;

public sealed partial class MainWindow
{
    private bool checkForUpdates = true;
    private UpdateChecker updateChecker = new();
    private Task updateCheck = Task.CompletedTask;
    private Button? updateNoticeButton;

    // Runs once the startup screen is gone, so it never delays the first view. The demo and its
    // smoke tests never contact the network; --preview-update answers with a synthetic release.
    private void StartUpdateCheck()
    {
        if (closed || !checkForUpdates || !updateCheck.IsCompleted)
            return;
        if (demo)
        {
            if (!Environment.GetCommandLineArgs().Contains("--preview-update"))
                return;
            var current = Version.Parse(AppVersion.Display.Split('-')[0]);
            string next = $"v{current.Major}.{current.Minor}.{current.Build + 1}";
            updateChecker = new(new SyntheticReleaseHandler(_ => SyntheticRelease(next)));
        }
        updateCheck = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var update = await updateChecker.CheckAsync(AppVersion.Display, lifetime.Token);
            if (update is not null && !closed && checkForUpdates)
                ShowUpdateNotice(update);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Offline, rate-limited or an unexpected response: the check is optional, so it is
            // not reported and simply runs again at the next start.
            Debug.WriteLine($"Update check failed: {error}");
        }
    }

    private void ShowUpdateNotice(AvailableUpdate update)
    {
        // An error awaiting review keeps priority.
        if (closed || HasPersistentNotification)
            return;
        ShowToast(
            Strings.Format("Update_AvailableMessage", update.Version),
            InfoBarSeverity.Informational,
            Strings.Get("Update_AvailableTitle"),
            replacePersistent: true
        );
        // The notice stays until it is closed; transient toasts do not replace it.
        toastTimer.Stop();
        updateNoticeButton = new Button { Content = Strings.Get("Update_OpenReleasePage") };
        updateNoticeButton.Click += (_, _) => OpenReleasePage(update.ReleasePage);
        MessageBar.ActionButton = updateNoticeButton;
    }

    private bool ShowingUpdateNotice =>
        MessageBar.IsOpen
        && updateNoticeButton is not null
        && MessageBar.ActionButton == updateNoticeButton;

    private void ChangeCheckForUpdates(bool check)
    {
        try
        {
            displaySettings?.SaveCheckForUpdates(check);
            checkForUpdates = check;
            if (check)
                StartUpdateCheck();
            else if (ShowingUpdateNotice)
                MessageBar.IsOpen = false;
        }
        catch (Exception error)
        {
            SettingsPage.SetCheckForUpdates(checkForUpdates);
            ShowError(error);
        }
    }

    // The application runs elevated. Explorer hands the address to the signed-in user's shell,
    // so the browser opens with normal rights instead of inheriting elevation. The address is
    // built from a validated version number, never taken from the response.
    private void OpenReleasePage(Uri page)
    {
        try
        {
            var start = new ProcessStartInfo(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "explorer.exe"
                )
            )
            {
                UseShellExecute = true,
            };
            start.ArgumentList.Add(page.AbsoluteUri);
            using var process = Process.Start(start);
            MessageBar.IsOpen = false;
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }
}
