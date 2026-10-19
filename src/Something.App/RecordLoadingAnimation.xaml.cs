using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
    private FrameworkElement FrontSprite => DefaultChick.Front;
    private FrameworkElement SideSprite => DefaultChick.Side;
    private RotateTransform LeftFootMotion => DefaultChick.LeftFoot;
    private RotateTransform RightFootMotion => DefaultChick.RightFoot;

    public RecordLoadingAnimation()
    {
        InitializeComponent();
        Storyboard.SetTarget(FrontFacingCycle, FrontSprite);
        Storyboard.SetTarget(SideFacingCycle, SideSprite);
        Storyboard.SetTarget(LeftFootCycle, LeftFootMotion);
        Storyboard.SetTarget(RightFootCycle, RightFootMotion);
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
            ChickMotion.TranslateX = ActiveKind switch
            {
                LoadingAnimationKind.Inspect => 136,
                LoadingAnimationKind.ScanDisk => 70,
                LoadingAnimationKind.DeleteHistory => 96,
                _ => 94,
            };
            ChickMotion.TranslateY = ActiveKind switch
            {
                LoadingAnimationKind.ScanDisk => 40,
                LoadingAnimationKind.DeleteHistory => 43,
                _ => 22,
            };
            FrontSprite.Opacity = 1;
            SideSprite.Opacity = 0;
            GateReady.Opacity = ActiveKind == LoadingAnimationKind.Inspect ? 1 : 0;
            if (ActiveKind == LoadingAnimationKind.DeleteHistory)
            {
                DefaultChick.SetFarewellPose(true);
                Farewell.Opacity = 1;
            }
            return;
        }
        settling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IsPlaying = true;
        BeginCycle();
    }

    internal void SetKind(LoadingAnimationKind kind, bool interruptCurrentCycle = false)
    {
        requestedKind = kind;
        // Foreground loading can preempt a background scan without settling its shared scope.
        if (interruptCurrentCycle && IsPlaying && ActiveKind != kind)
            BeginCycle();
    }

    internal Task WaitForSettledAsync() => settling?.Task ?? Task.CompletedTask;

    internal void SetPlaybackPaused(bool paused)
    {
        if (!IsPlaying)
            return;
        if (paused)
            activeCycle?.Pause();
        else
            activeCycle?.Resume();
    }

    internal void Cancel() => Stop();

    private void OnCycleCompleted(object? sender, object args)
    {
        CompletedCycles++;
        if (busy)
            BeginCycle();
        else
            Stop(preserveFinalFrame: ActiveKind == LoadingAnimationKind.OptimizeDatabase);
    }

    private void Stop(bool preserveFinalFrame = false)
    {
        busy = false;
        IsPlaying = false;
        // Stopping the storyboard restores the initial queue instead of the completed DB entry.
        if (!preserveFinalFrame)
            activeCycle?.Stop();
        settling?.TrySetResult();
        Settled?.Invoke(this, EventArgs.Empty);
    }
}
