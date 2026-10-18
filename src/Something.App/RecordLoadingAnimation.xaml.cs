using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Something.App;

public sealed partial class RecordLoadingAnimation : UserControl
{
    private bool busy;
    private TaskCompletionSource? settling;
    private Storyboard? activeCycle;
    private LoadingAnimationKind requestedKind;
    internal LoadingAnimationKind ActiveKind { get; private set; }
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
            ApplyKind();
            Chick.Opacity = 1;
            ChickMotion.TranslateX = ActiveKind == LoadingAnimationKind.Inspect ? 136 : 94;
            ChickMotion.TranslateY = 22;
            FrontSprite.Opacity = 1;
            SideSprite.Opacity = 0;
            GateReady.Opacity = ActiveKind == LoadingAnimationKind.Inspect ? 1 : 0;
            CarriedRecords.Opacity = ActiveKind == LoadingAnimationKind.DeleteHistory ? 1 : 0;
            return;
        }
        settling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IsPlaying = true;
        BeginCycle();
    }

    internal void SetKind(LoadingAnimationKind kind) => requestedKind = kind;

    internal Task WaitForSettledAsync() => settling?.Task ?? Task.CompletedTask;

    internal void Cancel() => Stop();

    private void OnCycleCompleted(object? sender, object args)
    {
        CompletedCycles++;
        if (busy)
            BeginCycle();
        else
            Stop();
    }

    private void Stop()
    {
        busy = false;
        IsPlaying = false;
        activeCycle?.Stop();
        settling?.TrySetResult();
        Settled?.Invoke(this, EventArgs.Empty);
    }
}
