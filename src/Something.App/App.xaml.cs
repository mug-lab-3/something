using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Something.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? window;
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = Environment.GetCommandLineArgs().Contains("--animation-preview")
            ? new AnimationPreviewWindow()
            : new MainWindow();
        window.Closed += (_, _) => window = null;
        window.Activate();
        // The UAC prompt can leave the elevated launch without foreground rights. Windows still
        // decides: when it declines, it flashes the taskbar button instead of stealing input.
        BringToFront(window);
    }

    internal void ActivateExistingWindow() =>
        dispatcher.TryEnqueue(() =>
        {
            if (window is not { } current)
                return; // Initial launch will activate the window, or shutdown is already complete.
            if (
                current.AppWindow.Presenter is OverlappedPresenter
                {
                    State: OverlappedPresenterState.Minimized
                } presenter
            )
                presenter.Restore();
            current.Activate();
            BringToFront(current);
        });

    private static void BringToFront(Window window) =>
        _ = SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
