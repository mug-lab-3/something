using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace Something.App;

public sealed partial class MainWindow
{
    private bool starting = true;
    private bool startupStarted;
    private nint? originalSplashStyle;
    private nint? originalSplashExtendedStyle;

    private double StartupRasterizationScale() =>
        Math.Max(96u, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))) / 96d;

    private void SetStartupWindowPresentation(bool splash, double scale)
    {
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        originalSplashStyle ??= GetWindowLongPtrW(window, -16);
        originalSplashExtendedStyle ??= GetWindowLongPtrW(window, -20);
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(!splash, !splash);
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = !splash;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            // Removing the title bar alone leaves DWM's one-pixel outline around the rounded corners.
            uint borderColor = splash ? 0xFFFFFFFEu : 0xFFFFFFFFu; // DWMWA_COLOR_NONE / DEFAULT
            Marshal.ThrowExceptionForHR(
                DwmSetWindowAttribute(
                    WinRT.Interop.WindowNative.GetWindowHandle(this),
                    34, // DWMWA_BORDER_COLOR
                    ref borderColor,
                    sizeof(uint)
                )
            );
        }
        AppTitleBar.Visibility = splash ? Visibility.Collapsed : Visibility.Visible;
        var workArea = DisplayArea
            .GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)
            .WorkArea;
        int width = Math.Min(
            (int)Math.Ceiling((splash ? 640 : 1280) * scale),
            (int)(workArea.Width * 0.95)
        );
        int height = Math.Min(
            (int)Math.Ceiling((splash ? 240 : 900) * scale),
            (int)(workArea.Height * 0.95)
        );
        AppWindow.MoveAndResize(
            new(
                workArea.X + (workArea.Width - width) / 2,
                workArea.Y + (workArea.Height - height) / 2,
                width,
                height
            )
        );
        const long captionFrame = 0x00C00000; // WS_BORDER | WS_DLGFRAME
        const long extendedFrame = 0x00000301; // WS_EX_DLGMODALFRAME | WINDOWEDGE | CLIENTEDGE
        long style = GetWindowLongPtrW(window, -16).ToInt64();
        long extendedStyle = GetWindowLongPtrW(window, -20).ToInt64();
        _ = SetWindowLongPtrW(
            window,
            -16,
            (nint)(
                (style & ~captionFrame)
                | (splash ? 0 : originalSplashStyle.Value.ToInt64() & captionFrame)
            )
        );
        _ = SetWindowLongPtrW(
            window,
            -20,
            (nint)(
                (extendedStyle & ~extendedFrame)
                | (splash ? 0 : originalSplashExtendedStyle.Value.ToInt64() & extendedFrame)
            )
        );
        // Recalculate the non-client area after removing/restoring the native frame styles.
        if (!SetWindowPos(window, 0, 0, 0, 0, 0, 0x0037)) // FRAMECHANGED | NOMOVE | NOSIZE | NOZORDER | NOACTIVATE
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags
    );

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        nint window,
        uint attribute,
        ref uint value,
        uint size
    );

    private async void OnStartupLoaded(object sender, RoutedEventArgs args)
    {
        if (startupStarted)
            return;
        startupStarted = true;
        startupTask = RunStartupAsync();
        await startupTask;
        StartUpdateCheck();
        if (!demo || closed)
            return;
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Contains("--preview-loading"))
        {
            await PreviewLoadingAnimationAsync();
            return;
        }
        int smoke = Array.IndexOf(arguments, "--smoke-test");
        if (smoke >= 0 && smoke + 1 < arguments.Length)
            await CaptureSmokeTestAsync(arguments[smoke + 1]);
    }

    private async Task RunStartupAsync()
    {
        StartupAnimation.SetBusy(true);
        try
        {
            if (demo)
            {
                var arguments = Environment.GetCommandLineArgs();
                if (arguments.Contains("--preview-dark"))
                    WindowLayout.RequestedTheme = ElementTheme.Dark;
                if (arguments.Contains("--preview-startup"))
                {
                    await CaptureStartupPreviewAsync();
                    await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
                }
                if (Environment.GetCommandLineArgs().Contains("--smoke-startup"))
                    await CaptureStartupPreviewAsync();
                if (!Environment.GetCommandLineArgs().Contains("--preview-loading"))
                    await StartScanAsync();
            }
            else
                await InitializeHistoryAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!closed)
                ShowError(error);
        }
        finally
        {
            // Ends after the minimum display time and a short fade, wherever the cycle is.
            StartupAnimation.SetBusy(false);
            await StartupAnimation.WaitForSettledAsync();
            if (!closed)
            {
                RootLayout.UpdateLayout();
                starting = false;
                SetStartupWindowPresentation(false, RootLayout.XamlRoot.RasterizationScale);
                if (demo)
                    VerifyStartupFrame(false);
                UpdateViewLoadingPresentation();
                RootLayout.Opacity = 1;
                StartupWorkspaceHost.IsEnabled = true;
                StartupOverlay.Visibility = Visibility.Collapsed;
                if (demo && (StartupAnimation.IsPlaying || !startupStarted))
                    throw new InvalidOperationException(
                        "Startup did not settle before presenting the workspace."
                    );
            }
        }
    }

    private void SetStartupMessage(string message)
    {
        if (starting && !closed)
            StartupText.Text = message;
    }

    private async Task CaptureStartupPreviewAsync()
    {
        await Task.Delay(500, lifetime.Token);
        VerifyStartupFrame(true);
        RootLayout.UpdateLayout();
        if (
            StartupOverlay.Visibility != Visibility.Visible
            || RootLayout.Opacity != 0
            || StartupWorkspaceHost.IsEnabled
            || AppTitleBar.Visibility != Visibility.Collapsed
            || WindowLayout.ActualWidth < WindowLayout.ActualHeight * 2
        )
            throw new InvalidOperationException(
                "Startup exposed an incomplete or interactive workspace."
            );
        var arguments = Environment.GetCommandLineArgs();
        int smoke = Array.IndexOf(arguments, "--smoke-test");
        if (smoke < 0 || smoke + 1 >= arguments.Length)
            return;
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(WindowLayout);
        using var file = File.Create(arguments[smoke + 1] + ".startup.png");
        using var output = file.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth,
            (uint)bitmap.PixelHeight,
            96,
            96,
            (await bitmap.GetPixelsAsync()).ToArray()
        );
        await encoder.FlushAsync();
        await CaptureStartupFrameAsync(arguments[smoke + 1]);
    }
}
