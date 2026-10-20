using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Something.App;

public sealed partial class RecordLoadingAnimation : UserControl
{
    private const double InspectPlaybackSpeedRatio = .55;
    internal const double InspectCycleSeconds = 1.82 / InspectPlaybackSpeedRatio;

    // Presentation timing shared by every mode. Work shorter than the delay shows nothing; once
    // shown, the animation stays long enough not to read as a flicker, then fades out wherever
    // the cycle is instead of waiting for its end.
    internal static readonly TimeSpan DefaultAppearDelay = TimeSpan.FromMilliseconds(300);
    internal static readonly TimeSpan MinimumVisible = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(200);

    private enum Phase
    {
        Idle,
        Pending,
        Shown,
        Fading,
    }

    private Phase phase;
    private bool busy;
    private bool animated;
    private long shownAt;
    private TaskCompletionSource? settling;
    private Storyboard? activeCycle;
    private Storyboard? fade;
    private readonly DispatcherTimer appearTimer = new();
    private readonly DispatcherTimer finishTimer = new();
    private LoadingAnimationKind requestedKind;
    internal LoadingAnimationKind ActiveKind { get; private set; }
    internal bool IsPlaying => phase != Phase.Idle;
    internal bool IsPresented => phase is Phase.Shown or Phase.Fading;
    internal bool IsAnimating => IsPresented && animated;
    internal TimeSpan LastPresentedFor { get; private set; }
    internal int CompletedCycles { get; private set; }
    internal event EventHandler? Settled;

    /// <summary>Delay before anything appears. Hosts that already delay their indicator use zero.</summary>
    internal TimeSpan AppearDelay { get; set; } = DefaultAppearDelay;

    /// <summary>The element shown and faded with the animation, such as a panel with its caption.</summary>
    internal UIElement? PresentationTarget { get; set; }

    private UIElement Target => PresentationTarget ?? this;
    private FrameworkElement FrontSprite => DefaultChick.Front;
    private FrameworkElement SideSprite => DefaultChick.Side;
    private RotateTransform LeftFootMotion => DefaultChick.LeftFoot;
    private RotateTransform RightFootMotion => DefaultChick.RightFoot;

    public RecordLoadingAnimation()
    {
        InitializeComponent();
        // Preserve synchronized poses and scanner effects; match the departure to ~190 DIP/s.
        Cycle.SpeedRatio = InspectPlaybackSpeedRatio;
        Storyboard.SetTarget(InspectGazeCycle, DefaultChick.Gaze);
        Storyboard.SetTarget(FrontFacingCycle, FrontSprite);
        Storyboard.SetTarget(SideFacingCycle, SideSprite);
        Storyboard.SetTarget(LeftFootCycle, LeftFootMotion);
        Storyboard.SetTarget(RightFootCycle, RightFootMotion);
        appearTimer.Tick += (_, _) =>
        {
            appearTimer.Stop();
            if (phase == Phase.Pending)
                Appear();
        };
        finishTimer.Tick += (_, _) =>
        {
            finishTimer.Stop();
            if (phase == Phase.Shown && !busy)
                BeginFade();
        };
        Unloaded += (_, _) => Stop();
    }

    internal void SetBusy(bool value) => SetBusy(value, new UISettings().AnimationsEnabled);

    internal void SetBusy(bool value, bool animationsEnabled)
    {
        busy = value;
        if (value)
        {
            switch (phase)
            {
                case Phase.Idle:
                    animated = animationsEnabled;
                    settling = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    phase = Phase.Pending;
                    Target.Opacity = 0;
                    ApplyKind(); // Hidden until it appears, but the requested mode is settled now.
                    if (AppearDelay <= TimeSpan.Zero)
                        Appear();
                    else
                    {
                        appearTimer.Interval = AppearDelay;
                        appearTimer.Start();
                    }
                    break;
                case Phase.Shown:
                    finishTimer.Stop();
                    break;
                case Phase.Fading:
                    // New work arrived during the fade: keep the running cycle and show it again.
                    fade?.Stop();
                    Target.Opacity = 1;
                    phase = Phase.Shown;
                    break;
            }
            return;
        }
        switch (phase)
        {
            case Phase.Pending:
                // Finished before anything appeared: no indicator and no wait.
                Stop();
                break;
            case Phase.Shown:
                var remaining = MinimumVisible - Stopwatch.GetElapsedTime(shownAt);
                if (remaining <= TimeSpan.Zero)
                    BeginFade();
                else
                {
                    finishTimer.Interval = remaining;
                    finishTimer.Start();
                }
                break;
        }
    }

    private void Appear()
    {
        phase = Phase.Shown;
        shownAt = Stopwatch.GetTimestamp();
        if (animated)
            BeginCycle();
        else
            ApplyStaticPose();
        Target.Opacity = 1;
    }

    private void ApplyStaticPose()
    {
        ApplyKind();
        Chick.Opacity = 1;
        ChickMotion.TranslateX = ActiveKind switch
        {
            LoadingAnimationKind.Inspect => 136,
            LoadingAnimationKind.ScanDisk => 132,
            LoadingAnimationKind.DeleteHistory => 96,
            LoadingAnimationKind.Export => ExportChickStop,
            _ => 94,
        };
        ChickMotion.TranslateY = ActiveKind switch
        {
            LoadingAnimationKind.ScanDisk => -20,
            LoadingAnimationKind.DeleteHistory => 43,
            _ => 22,
        };
        FrontSprite.Opacity = 1;
        SideSprite.Opacity = 0;
        GateReady.Opacity = ActiveKind == LoadingAnimationKind.Inspect ? 1 : 0;
        if (ActiveKind == LoadingAnimationKind.ScanDisk)
        {
            CraneTravel.X = 220;
            CraneLift.Y = 8;
            CraneCable.ScaleY = 4;
            CraneLeftArm.Angle = CraneRightArm.Angle = 0;
        }
        if (ActiveKind == LoadingAnimationKind.DeleteHistory)
        {
            DefaultChick.SetFarewellPose(true);
            Farewell.Opacity = 1;
        }
        if (ActiveKind == LoadingAnimationKind.Export)
        {
            // Hold the sheet above the head beside the file.
            ExportSheetMotion.TranslateX = ExportChickStop;
            ExportSheetMotion.TranslateY = 2;
            ExportSheet.Opacity = 1;
            Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(Chick, 2);
            Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(ExportSheet, 3);
        }
    }

    private void BeginFade()
    {
        phase = Phase.Fading;
        if (!animated)
        {
            Stop();
            return;
        }
        if (fade is null)
        {
            fade = new Storyboard();
            fade.Children.Add(new DoubleAnimation { To = 0, Duration = FadeOut });
            fade.Completed += (_, _) =>
            {
                if (phase == Phase.Fading)
                    Stop();
            };
        }
        fade.Stop();
        Storyboard.SetTarget(fade.Children[0], Target);
        Storyboard.SetTargetProperty(fade.Children[0], "Opacity");
        fade.Begin();
    }

    internal void SetKind(LoadingAnimationKind kind, bool interruptCurrentCycle = false)
    {
        requestedKind = kind;
        if (phase == Phase.Pending)
            ApplyKind();
        // Foreground loading can preempt a background scan without settling its shared scope.
        else if (interruptCurrentCycle && IsPresented && animated && ActiveKind != kind)
            BeginCycle();
    }

    internal Task WaitForSettledAsync() => settling?.Task ?? Task.CompletedTask;

    internal void SetPlaybackPaused(bool paused)
    {
        if (!IsPresented)
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
        // Cycles repeat until the presentation ends; ending never waits for a cycle boundary.
        if (IsPresented)
            BeginCycle();
    }

    private void Stop()
    {
        if (IsPresented)
            LastPresentedFor = Stopwatch.GetElapsedTime(shownAt);
        appearTimer.Stop();
        finishTimer.Stop();
        busy = false;
        phase = Phase.Idle;
        fade?.Stop();
        activeCycle?.Stop();
        Target.Opacity = 0;
        settling?.TrySetResult();
        Settled?.Invoke(this, EventArgs.Empty);
    }
}
