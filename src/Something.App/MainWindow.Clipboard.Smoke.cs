using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Something.App;

public sealed partial class MainWindow
{
    private async Task VerifyClipboardAsync()
    {
        if (!demo)
            throw new InvalidOperationException("Clipboard smoke requires synthetic paths.");
        foreach (
            string path in new[]
            {
                @"C:\",
                @"C:\Users\Example",
                @"C:\資料 🐣\report.txt",
                @"\\server\share\folder",
            }
        )
        {
            await CopyPathAsync(path);
            if (
                !MessageBar.IsOpen
                || !toastTimer.IsEnabled
                || MessageBar.Severity != Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success
                || MessageBar.Title != Strings.Get("Clipboard_Copied")
                || MessageBar.Message != path
            )
                throw new InvalidOperationException(
                    "Path copy did not show its success toast and copied path."
                );
            if (HasPersistentNotification)
                throw new InvalidOperationException(
                    "Path copy displayed an error: " + MessageBar.Message
                );
            string actual = await ReadClipboardForSmokeAsync();
            if (!string.Equals(path, actual, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Copied path did not round-trip as UTF-16 text."
                );
        }
    }

    // Verify the desktop clipboard directly: the WinRT API itself fails with RPC_E_WRONG_THREAD here.
    private async Task<string> ReadClipboardForSmokeAsync()
    {
        for (
            int attempt = 0;
            !OpenClipboard(WinRT.Interop.WindowNative.GetWindowHandle(this));
            attempt++
        )
        {
            if (attempt == 4)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            await Task.Delay(50, lifetime.Token);
        }
        try
        {
            nint memory = GetClipboardData(13);
            nuint bytes = GlobalSize(memory);
            if (memory == 0 || bytes is < 2 or > 131072 || bytes % 2 != 0)
                throw new InvalidOperationException(
                    "Copied text has an invalid clipboard allocation."
                );
            nint buffer = GlobalLock(memory);
            if (buffer == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                string text = Marshal.PtrToStringUni(buffer, checked((int)bytes / 2))!;
                int terminator = text.IndexOf('\0');
                return terminator >= 0
                    ? text[..terminator]
                    : throw new InvalidOperationException("Clipboard text is not terminated.");
            }
            finally
            {
                _ = GlobalUnlock(memory);
            }
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetClipboardData(uint format);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nuint GlobalSize(nint memory);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);
}
