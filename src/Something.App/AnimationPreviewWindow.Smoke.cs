using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace Something.App;

public sealed partial class AnimationPreviewWindow
{
    private async Task RunSmokeAsync(string path)
    {
        try
        {
            for (int index = 0; index < 4; index++)
            {
                KindBox.SelectedIndex = index;
                Restart();
                if (!Animation.IsPlaying)
                    throw new InvalidOperationException("Standalone preview did not start.");
                double[] frames = index switch
                {
                    1 => [.55, .98, 1.6, 2.45, 2.8, 3.2, 3.7],
                    2 => [.4, 1.5, 2.5, 3.08],
                    3 => [.4, 1.15, 2.5, 2.86, 3.85, 4.8],
                    _ => [.6],
                };
                foreach (double frame in frames)
                {
                    Animation.PauseForPreview(frame);
                    await Task.Delay(80);
                    if (index == 3)
                        Animation.VerifyOptimizationPose(frame);
                    else if (index == 2)
                        Animation.VerifyDeletionPose(frame);
                    else if (index == 1)
                        Animation.VerifyScanPose(frame);
                    if (index != 2)
                        Animation.VerifyDepartureAccessoriesHidden();
                    await CaptureAsync($"{path}.{index}-{frame:F2}.png");
                }
                Animation.ResumeAfterPreview();
                Animation.SetBusy(false);
                await Animation.WaitForSettledAsync().WaitAsync(TimeSpan.FromSeconds(4));
                if (Animation.IsPlaying)
                    throw new InvalidOperationException("Standalone preview did not settle.");
                await Task.Delay(80);
                Animation.VerifySettledPose();
                await CaptureAsync($"{path}.{index}-settled.png");
                StaticBox.IsChecked = true;
                Restart();
                if (Animation.IsPlaying || !Animation.WaitForSettledAsync().IsCompleted)
                    throw new InvalidOperationException("Static preview started an animation.");
                if (index == 2)
                    Animation.VerifyDeletionPose(1.5);
                StaticBox.IsChecked = false;
            }
            KindBox.SelectedIndex = 3;
            ThemeBox.SelectedIndex = 1;
            Restart();
            Animation.PauseForPreview(2.86);
            await Task.Delay(80);
            await CaptureAsync(path + ".light.png");
            KindBox.SelectedIndex = 2;
            Restart();
            Animation.PauseForPreview(1.5);
            await Task.Delay(80);
            Animation.VerifyDeletionPose(1.5);
            await CaptureAsync(path + ".farewell-light.png");
            Animation.Cancel();
            await File.WriteAllTextAsync(
                path + ".result",
                "Standalone animation preview passed: all four modes, backpack departure and raised-wing farewell, identical full-size chicks, conversation, visual whistle cue, queue alignment, sequential entry, light/dark, static mode, settling and cancellation. No MainWindow, history DB or live volume was opened."
            );
        }
        catch (Exception error)
        {
            Animation.Cancel();
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(path + ".result", error.ToString());
        }
        finally
        {
            Close();
        }
    }

    private async Task CaptureAsync(string path)
    {
        Root.UpdateLayout();
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(Root);
        byte[] pixels = (await bitmap.GetPixelsAsync()).ToArray();
        using var file = File.Create(path);
        using var stream = file.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
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
