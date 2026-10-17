using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Something.App;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    private void InitializeNotifications()
    {
        toastTimer.Tick += (_, _) =>
        {
            toastTimer.Stop();
            MessageBar.IsOpen = false;
        };
        MessageBar.Closed += (_, _) => toastTimer.Stop();
    }

    private void ShowToast(
        string message,
        InfoBarSeverity severity,
        string title = "",
        bool replacePersistent = false
    )
    {
        if (closed)
            return;
        bool transient = severity is InfoBarSeverity.Success or InfoBarSeverity.Informational;
        // A different drive completing must not hide an error awaiting review.
        if (transient && HasPersistentNotification && !replacePersistent)
            return;
        toastTimer.Stop();
        MessageBar.Title = string.IsNullOrWhiteSpace(title)
            ? ToastPresentation.GetTitle(severity)
            : title;
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.Style = ToastPresentation.GetStyle(severity);
        MessageBar.IsOpen = true;
        if (transient)
            toastTimer.Start();
    }

    private bool HasPersistentNotification =>
        MessageBar.IsOpen
        && MessageBar.Severity is InfoBarSeverity.Warning or InfoBarSeverity.Error;

    private async Task VerifyNotificationsAsync()
    {
        ShowToast("Synthetic scan completed", InfoBarSeverity.Success);
        if (!MessageBar.IsOpen || !toastTimer.IsEnabled)
            throw new InvalidOperationException("Completion toast was not shown.");
        await Task.Delay(TimeSpan.FromSeconds(6.2), lifetime.Token);
        if (MessageBar.IsOpen || toastTimer.IsEnabled)
            throw new InvalidOperationException("Completion toast did not dismiss automatically.");
        ShowToast("Synthetic failure", InfoBarSeverity.Error);
        ShowToast("Another drive completed", InfoBarSeverity.Success);
        if (
            !HasPersistentNotification
            || toastTimer.IsEnabled
            || MessageBar.Message != "Synthetic failure"
        )
            throw new InvalidOperationException("Completion hid a persistent failure.");
        MessageBar.IsOpen = false;
        ShowToast("Synthetic cancellation", InfoBarSeverity.Informational);
        if (!MessageBar.IsOpen || !toastTimer.IsEnabled)
            throw new InvalidOperationException("Cancellation toast was not shown.");
        MessageBar.IsOpen = false;
        if (toastTimer.IsEnabled)
            throw new InvalidOperationException("Dismissed toast retained its timer.");
    }
}
