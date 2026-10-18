using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace Something.App;

public sealed partial class MainWindow
{
    private void VerifyStartupFrame(bool splash)
    {
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        long caption = GetWindowLongPtrW(window, -16).ToInt64() & 0x00C00000;
        long edge = GetWindowLongPtrW(window, -20).ToInt64() & 0x00000301;
        if (
            caption != (splash ? 0 : originalSplashStyle!.Value.ToInt64() & 0x00C00000)
            || edge != (splash ? 0 : originalSplashExtendedStyle!.Value.ToInt64() & 0x00000301)
        )
            throw new InvalidOperationException(
                "The native splash frame was not removed/restored."
            );
    }

    // Capture our own native window, including non-client pixels absent from RenderTargetBitmap.
    private async Task CaptureStartupFrameAsync(string path)
    {
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!GetWindowRect(window, out var bounds))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        int width = bounds.Right - bounds.Left;
        int height = bounds.Bottom - bounds.Top;
        nint source = GetWindowDC(window);
        nint destination = CreateCompatibleDC(source);
        var info = new BitmapInfo
        {
            Size = 40,
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
        };
        nint bitmap = CreateDIBSection(source, ref info, 0, out nint pixels, 0, 0);
        if (source == 0 || destination == 0 || bitmap == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        nint previous = SelectObject(destination, bitmap);
        try
        {
            if (!PrintWindow(window, destination, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            byte[] bytes = new byte[checked(width * height * 4)];
            Marshal.Copy(pixels, bytes, 0, bytes.Length);
            for (int offset = 3; offset < bytes.Length; offset += 4)
                bytes[offset] = 255;
            using var file = File.Create(path + ".window.png");
            using var output = file.AsRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                (uint)width,
                (uint)height,
                96,
                96,
                bytes
            );
            await encoder.FlushAsync();
            _ = DwmGetWindowAttribute(window, 34, out uint border, sizeof(uint));
            await File.WriteAllTextAsync(
                path + ".frame.txt",
                $"style=0x{GetWindowLongPtrW(window, -16).ToInt64():X}; extended-style=0x{GetWindowLongPtrW(window, -20).ToInt64():X}; border=0x{border:X8}",
                lifetime.Token
            );
        }
        finally
        {
            _ = SelectObject(destination, previous);
            _ = DeleteObject(bitmap);
            _ = DeleteDC(destination);
            _ = ReleaseDC(window, source);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left,
            Top,
            Right,
            Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width,
            Height;
        public ushort Planes,
            BitCount;
        public uint Compression,
            ImageSize;
        public int XPixelsPerMeter,
            YPixelsPerMeter;
        public uint ColorsUsed,
            ColorsImportant,
            Color;
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindowDC(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint CreateCompatibleDC(nint source);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint CreateDIBSection(
        nint dc,
        ref BitmapInfo info,
        uint usage,
        out nint pixels,
        nint section,
        uint offset
    );

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint SelectObject(nint dc, nint value);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint window, nint dc, uint flags);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(
        nint window,
        uint attribute,
        out uint value,
        uint size
    );
}
