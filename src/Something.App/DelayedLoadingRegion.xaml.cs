using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Something.App;

public sealed partial class DelayedLoadingRegion : UserControl
{
    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body),
        typeof(object),
        typeof(DelayedLoadingRegion),
        new PropertyMetadata(null)
    );

    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    /// <summary>How visible the body is now: 1 normally, 0 while the animation covers it. As in
    /// the whole-view loading screen, the old content is not shown behind the animation.</summary>
    internal double BodyOpacity => BodyHost.Opacity;

    private readonly DelayedActivity activity;
    private bool pending;
    private bool presentationStarted;
    internal bool IsPending => !BodyHost.IsEnabled;
    internal bool IsIndicatorVisible => Indicator.Visibility == Visibility.Visible;
    internal bool IsPlaying => Animation.IsPlaying;
    internal int CompletedCycles => Animation.CompletedCycles;
    internal TimeSpan LastPresentedFor => Animation.LastPresentedFor;

    public DelayedLoadingRegion()
    {
        InitializeComponent();
        // DelayedActivity already waits before showing the indicator; do not delay twice.
        Animation.AppearDelay = TimeSpan.Zero;
        Animation.PresentationTarget = IndicatorPanel;
        activity = new(
            (pending, visible) =>
            {
                this.pending = pending;
                presentationStarted |= visible;
                UpdatePresentation();
            }
        );
        Animation.Settled += (_, _) =>
        {
            if (IsLoaded)
                UpdatePresentation();
        };
        Loaded += (_, _) => UpdatePresentation();
    }

    /// <summary>Raised when the animation has just appeared, so that something that belongs with
    /// it (a cancel button) can appear with it and not before it.</summary>
    internal event EventHandler? IndicatorShown;

    private void UpdatePresentation()
    {
        bool wasShown = Indicator.Visibility == Visibility.Visible;
        if (pending && presentationStarted)
            Indicator.Visibility = Visibility.Visible;
        Animation.SetBusy(pending && presentationStarted && IsLoaded);
        bool covered = presentationStarted && (pending || Animation.IsPlaying);
        BodyHost.IsEnabled = !pending && !covered;
        BodyHost.Opacity = covered ? 0 : 1;
        Indicator.Visibility = covered ? Visibility.Visible : Visibility.Collapsed;
        if (!pending && !Animation.IsPlaying)
            presentationStarted = false;
        if (!wasShown && Indicator.Visibility == Visibility.Visible)
            IndicatorShown?.Invoke(this, EventArgs.Empty);
    }

    internal sealed class UpdateLease(DelayedLoadingRegion region, IDisposable lease)
        : IDisposable,
            IAsyncDisposable
    {
        public void Dispose() => lease.Dispose();

        public async ValueTask DisposeAsync()
        {
            Dispose();
            if (!region.pending)
                await region.WaitForSettledAsync();
        }
    }

    internal UpdateLease BeginUpdate() => new(this, activity.Begin());

    internal Task WaitForSettledAsync() => Animation.WaitForSettledAsync();

    internal void Cancel()
    {
        pending = false;
        presentationStarted = false;
        activity.Dispose();
        Animation.Cancel();
    }
}
