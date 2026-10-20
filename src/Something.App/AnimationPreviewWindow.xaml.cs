using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Something.App;

public sealed partial class AnimationPreviewWindow : Window
{
    private bool ready;
    private int playbackVersion;

    public AnimationPreviewWindow()
    {
        InitializeComponent();
        // The preview shows motion at once; production hosts keep the appearance delay.
        Animation.AppearDelay = TimeSpan.Zero;
        Title = Strings.Format("AnimationPreview_WindowTitle", AppVersion.WindowTitle);
        AppWindow.Resize(new SizeInt32(780, 560));
        KindBox.SelectedIndex = 3;
        Root.Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            ready = false;
            Animation.Cancel();
        };
    }

    private void Restart()
    {
        if (!ready)
            return;
        playbackVersion++;
        Animation.Cancel();
        Animation.SetKind(
            KindBox.SelectedIndex switch
            {
                1 => LoadingAnimationKind.ScanDisk,
                2 => LoadingAnimationKind.DeleteHistory,
                3 => LoadingAnimationKind.OptimizeDatabase,
                _ => LoadingAnimationKind.Inspect,
            }
        );
        PauseButton.IsChecked = false;
        PauseButton.IsEnabled = StaticBox.IsChecked != true;
        Animation.SetBusy(true, animationsEnabled: StaticBox.IsChecked != true);
        StateText.Text =
            StaticBox.IsChecked == true
                ? Strings.Get("AnimationPreview_Static")
                : Strings.Get("AnimationPreview_Playing");
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs args) => Restart();

    private void OnRestart(object sender, RoutedEventArgs args) => Restart();

    private void OnStaticChanged(object sender, RoutedEventArgs args) => Restart();

    private void OnThemeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (Root is not null)
            Root.RequestedTheme =
                ThemeBox.SelectedIndex == 1 ? ElementTheme.Light : ElementTheme.Dark;
    }

    private void OnPause(object sender, RoutedEventArgs args)
    {
        Animation.SetPlaybackPaused(PauseButton.IsChecked == true);
        StateText.Text =
            PauseButton.IsChecked == true
                ? Strings.Get("AnimationPreview_Paused")
                : Strings.Get("AnimationPreview_Playing");
    }

    private async void OnFinish(object sender, RoutedEventArgs args)
    {
        int version = playbackVersion;
        PauseButton.IsChecked = false;
        Animation.SetPlaybackPaused(false);
        Animation.SetBusy(false);
        await Animation.WaitForSettledAsync();
        if (ready && version == playbackVersion)
        {
            StateText.Text = Strings.Get("AnimationPreview_Stopped");
            PauseButton.IsEnabled = false;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (ready)
            return;
        ready = true;
        Restart();
        var arguments = Environment.GetCommandLineArgs();
        int smoke = Array.IndexOf(arguments, "--animation-preview-smoke");
        if (smoke >= 0 && smoke + 1 < arguments.Length)
            await RunSmokeAsync(arguments[smoke + 1]);
    }
}
