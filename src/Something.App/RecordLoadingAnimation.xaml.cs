using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace Something.App;

public sealed partial class RecordLoadingAnimation : UserControl
{
    private bool busy;
    private TaskCompletionSource? settling;
    internal bool IsPlaying { get; private set; }
    internal int CompletedCycles { get; private set; }
    internal event EventHandler? Settled;

    public RecordLoadingAnimation()
    {
        InitializeComponent();
        Unloaded += (_, _) => Stop();
    }

    internal void SetBusy(bool value) => SetBusy(value, new UISettings().AnimationsEnabled);

    internal void SetBusy(bool value, bool animationsEnabled)
    {
        busy = value;
        if (!value || IsPlaying)
            return;
        if (!animationsEnabled)
        {
            // Keep the chick beside its nest when system animations are disabled.
            Chick.Opacity = 1;
            ChickMotion.TranslateX = 106;
            ChickMotion.TranslateY = 22;
            FrontSprite.Opacity = 1;
            SideSprite.Opacity = 0;
            GateReady.Opacity = 1;
            return;
        }
        settling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IsPlaying = true;
        Cycle.Begin();
    }

    internal Task WaitForSettledAsync() => settling?.Task ?? Task.CompletedTask;

    internal void Cancel() => Stop();

    private void OnCycleCompleted(object? sender, object args)
    {
        CompletedCycles++;
        if (busy)
            Cycle.Begin();
        else
            Stop();
    }

    private void Stop()
    {
        busy = false;
        IsPlaying = false;
        Cycle.Stop();
        settling?.TrySetResult();
        Settled?.Invoke(this, EventArgs.Empty);
    }
}
