using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace Something.App;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        string key = $"Something.Main.{identity.User!.Value}.{process.SessionId}";
        // Synthetic previews never redirect a real user's running application.
        if (args.Contains("--animation-preview"))
            key += ".AnimationPreview";
        else if (args.Contains("--demo"))
            key += ".Demo";
        var instance = AppInstance.FindOrRegisterForKey(key);
        if (!instance.IsCurrent)
        {
            try
            {
                await instance
                    .RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs())
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(10));
                return 0;
            }
            catch (Exception error) when (error is COMException or TimeoutException)
            {
                // Do not open a second database if the existing instance is closing or unresponsive.
                return 1;
            }
        }

        App? app = null;
        void Activate(object? sender, AppActivationArguments arguments) =>
            app?.ActivateExistingWindow();
        instance.Activated += Activate;
        try
        {
            // The primary path has not awaited: WinUI starts on this original STA thread.
            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread())
                );
                app = new App();
            });
            return Environment.ExitCode;
        }
        finally
        {
            instance.Activated -= Activate;
            // Keep the registration until the UI and asynchronous shutdown have finished.
            instance.UnregisterKey();
        }
    }
}
