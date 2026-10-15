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

    private readonly DelayedActivity activity;
    private bool pending;
    private bool presentationStarted;
    internal bool IsPending => !BodyHost.IsEnabled;
    internal bool IsIndicatorVisible => Indicator.Visibility == Visibility.Visible;
    internal bool IsPlaying => Animation.IsPlaying;
    internal int CompletedCycles => Animation.CompletedCycles;

    public DelayedLoadingRegion()
    {
        InitializeComponent();
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
