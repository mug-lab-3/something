using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Something.App;

internal enum LoadingAnimationKind
{
    Inspect,
    DeleteHistory,
    OptimizeDatabase,
    ScanDisk,
}

public sealed partial class RecordLoadingAnimation
{
    private readonly Dictionary<LoadingAnimationKind, Storyboard> maintenanceCycles = [];

    private void ApplyKind()
    {
        ActiveKind = requestedKind;
        bool inspect = ActiveKind == LoadingAnimationKind.Inspect;
        bool scanning = ActiveKind == LoadingAnimationKind.ScanDisk;
        bool optimizing = ActiveKind == LoadingAnimationKind.OptimizeDatabase;
        DiskSource.Visibility = DiskFront.Visibility = scanning
            ? Visibility.Visible
            : Visibility.Collapsed;
        ScanCrane.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(DiskSource, scanning ? 28 : 0);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(DiskFront, scanning ? 28 : 0);
        Width = optimizing ? 420 : 300;
        AnimationClip.Rect = new global::Windows.Foundation.Rect(0, 0, Width, Height);
        // Match the DB/nest baseline while leaving room above it for the scan crane.
        SceneOffset.Y = scanning ? 0 : 28;
        DatabaseBackMotion.X = DatabaseFrontMotion.X =
            optimizing ? 250
            : scanning ? 132
            : 0;
        DatabaseBackMotion.Y = DatabaseFrontMotion.Y = scanning ? 28 : 0;
        Chick.Visibility = optimizing ? Visibility.Collapsed : Visibility.Visible;
        OptimizeFirst.Visibility =
            OptimizeSecond.Visibility =
            OptimizeThird.Visibility =
                optimizing ? Visibility.Visible : Visibility.Collapsed;
        OptimizationConversation.Visibility = OptimizationWhistle.Visibility = optimizing
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (optimizing)
            SetOptimizationPose();
        ScannerArch.Visibility =
            ScannerLamp.Visibility =
            GateReady.Visibility =
            ScanBeam.Visibility =
                inspect ? Visibility.Visible : Visibility.Collapsed;
        bool deleting = ActiveKind == LoadingAnimationKind.DeleteHistory;
        DefaultChick.WearBackpack(deleting);
        DefaultChick.SetFarewellPose(false);
        Farewell.Visibility = deleting ? Visibility.Visible : Visibility.Collapsed;
        Farewell.Opacity = 0;
    }

    private void BeginCycle()
    {
        activeCycle?.Stop();
        ApplyKind();
        if (ActiveKind == LoadingAnimationKind.Inspect)
            activeCycle = Cycle;
        else
        {
            if (!maintenanceCycles.TryGetValue(ActiveKind, out var storyboard))
            {
                storyboard = ActiveKind switch
                {
                    LoadingAnimationKind.ScanDisk => CreateScanCycle(),
                    LoadingAnimationKind.OptimizeDatabase => CreateOptimizationCycle(),
                    _ => CreateDeletionCycle(),
                };
                maintenanceCycles.Add(ActiveKind, storyboard);
            }
            activeCycle = storyboard;
        }
        activeCycle.Begin();
    }
}
