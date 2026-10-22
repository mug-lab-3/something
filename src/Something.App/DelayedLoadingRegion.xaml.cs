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

    public static readonly DependencyProperty FillsRegionProperty = DependencyProperty.Register(
        nameof(FillsRegion),
        typeof(bool),
        typeof(DelayedLoadingRegion),
        new PropertyMetadata(false, (d, _) => ((DelayedLoadingRegion)d).UpdateAnimationSize())
    );

    /// <summary>Whether the animation is enlarged to fill most of the region instead of keeping
    /// its own size; for a region that is a whole screen, where a small animation is lost.</summary>
    public bool FillsRegion
    {
        get => (bool)GetValue(FillsRegionProperty);
        set => SetValue(FillsRegionProperty, value);
    }

    // The animation is 300 by 200. Enlarged, it keeps that shape and leaves room below for its
    // text and for a button at the bottom of the region.
    private const double AnimationWidth = 300,
        AnimationHeight = 200;

    /// <summary>How much the animation is enlarged now; 1 when it keeps its own size.</summary>
    internal double AnimationScaleFactor => AnimationScale.ScaleX;

    private void UpdateAnimationSize()
    {
        double scale = 1;
        if (FillsRegion && ActualWidth > 0 && ActualHeight > 0)
            scale = Math.Clamp(
                Math.Min(
                    ActualWidth * 0.8 / AnimationWidth,
                    (ActualHeight - 140) * 0.9 / AnimationHeight
                ),
                1,
                4
            );
        AnimationScale.ScaleX = AnimationScale.ScaleY = scale;
        AnimationHost.Width = AnimationWidth * scale;
        AnimationHost.Height = AnimationHeight * scale;
    }

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
        SizeChanged += (_, _) => UpdateAnimationSize();
    }

    private void UpdatePresentation()
    {
        if (pending && presentationStarted)
            Indicator.Visibility = Visibility.Visible;
        Animation.SetBusy(pending && presentationStarted && IsLoaded);
        bool covered = presentationStarted && (pending || Animation.IsPlaying);
        BodyHost.IsEnabled = !pending && !covered;
        BodyHost.Opacity = covered ? 0.45 : 1;
        Indicator.Visibility = covered ? Visibility.Visible : Visibility.Collapsed;
        if (!pending && !Animation.IsPlaying)
            presentationStarted = false;
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
