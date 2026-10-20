using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Something.Windows;

/// <summary>Copies UTF-16 text using an explicit desktop window owner, including elevated windows.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsTextClipboard
{
    public static async Task SetTextAsync(
        nint owner,
        string text,
        CancellationToken token = default
    )
    {
        ArgumentNullException.ThrowIfNull(text);
        if (owner == 0)
            throw new ArgumentException("A clipboard owner window is required.", nameof(owner));
        if (text.Contains('\0'))
            throw new ArgumentException(
                "Clipboard text must not contain a null character.",
                nameof(text)
            );
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (OpenClipboard(owner))
            {
                try
                {
                    WriteText(text);
                    return;
                }
                finally
                {
                    _ = CloseClipboard();
                }
            }
            int error = Marshal.GetLastPInvokeError();
            if (attempt == 4)
                throw new Win32Exception(error == 0 ? 170 : error);
            // Other processes briefly open the shared clipboard. Never block the UI thread waiting.
            await Task.Delay(50, token);
        }
    }

    private static void WriteText(string text)
    {
        const uint movableAndZeroInitialized = 0x0042;
        nint memory = GlobalAlloc(movableAndZeroInitialized, checked((nuint)(text.Length + 1) * 2));
        if (memory == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            nint buffer = GlobalLock(memory);
            if (buffer == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, buffer, text.Length);
            }
            finally
            {
                _ = GlobalUnlock(memory);
            }
            if (
                !EmptyClipboard()
                || SetClipboardData(
                    13 /* CF_UNICODETEXT */
                    ,
                    memory
                ) == 0
            )
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            // Windows owns the movable allocation after SetClipboardData succeeds. No Flush is needed.
            memory = 0;
        }
        finally
        {
            if (memory != 0)
                _ = GlobalFree(memory);
        }
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GlobalFree(nint memory);
}
