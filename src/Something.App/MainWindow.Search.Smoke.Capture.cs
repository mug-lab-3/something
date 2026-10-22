using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace Something.App;

public sealed partial class MainWindow
{
    // Writes what an element shows as a PNG, for looking at the screens the checks leave out.
    private static async Task RenderPngAsync(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0)
            throw new InvalidOperationException("The element did not render.");
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        using var file = File.Open(path, FileMode.Create, FileAccess.ReadWrite);
        using var output = file.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth,
            (uint)bitmap.PixelHeight,
            96,
            96,
            pixels
        );
        await encoder.FlushAsync();
    }
}
