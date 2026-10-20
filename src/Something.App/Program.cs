using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace Something.App;

internal static class Program
{
    // Main must stay synchronous: for an async Main the compiler generates the real entry point
    // without [STAThread], so the UI thread became MTA and the IME (TSF) could not attach.
    [STAThread]
    private static int Main(string[] args)
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
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            try
            {
                // The redirect runs on the thread pool; the STA thread only waits for it.
                return Task.Run(() => instance.RedirectActivationToAsync(activation).AsTask())
                    .Wait(TimeSpan.FromSeconds(10))
                    ? 0
                    : 1;
            }
            catch (AggregateException error) when (error.InnerException is COMException)
            {
                // Do not open a second database if the existing instance is closing or unresponsive.
                return 1;
            }
        }

        AppLanguage.Apply(args);
        App? app = null;
        void Activate(object? sender, AppActivationArguments arguments) =>
            app?.ActivateExistingWindow();
        instance.Activated += Activate;
        try
        {
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
            // A restart starts only now, so the new instance does not redirect to this one.
            AppLanguage.RestartIfRequested();
        }
    }
}
