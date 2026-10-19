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
        Width = optimizing ? 420 : 300;
        AnimationClip.Rect = new global::Windows.Foundation.Rect(0, 0, Width, 170);
        DatabaseBackMotion.X = DatabaseFrontMotion.X =
            optimizing ? 250
            : scanning ? 132
            : 0;
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
        SortingBox.Visibility =
            ActiveKind == LoadingAnimationKind.DeleteHistory
                ? Visibility.Visible
                : Visibility.Collapsed;
        CarriedRecords.Opacity = 0;
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

    private Storyboard CreateDeletionCycle()
    {
        var storyboard = new Storyboard();
        storyboard.Completed += OnCycleCompleted;
        Frames(
            ChickMotion,
            "TranslateX",
            [(0, 0), (.18, 0), (.46, 92), (.78, 136), (.96, 136), (1.4, 260)]
        );
        Frames(
            ChickMotion,
            "TranslateY",
            [
                (0, 35),
                (.18, -24),
                (.3, -38),
                (.46, 22),
                (.6, 19),
                (.7, 22),
                (.78, 19),
                (.88, 22),
                (.96, 22),
                (1.08, 19),
                (1.2, 22),
                (1.3, 19),
                (1.4, 22),
            ]
        );
        Frames(Chick, "Opacity", [(0, 0), (.12, 1), (1.39, 1), (1.4, 0)]);
        Frames(FrontSprite, "Opacity", [(0, 1), (.4, 0), (1.4, 0)], discrete: true);
        Frames(SideSprite, "Opacity", [(0, 0), (.4, 1), (1.4, 1)], discrete: true);
        Frames(
            LeftFootMotion,
            "Angle",
            [
                (0, 0),
                (.46, -20),
                (.58, 20),
                (.7, -20),
                (.82, 20),
                (.96, 0),
                (1.08, -25),
                (1.2, 25),
                (1.3, -25),
                (1.4, 0),
            ]
        );
        Frames(
            RightFootMotion,
            "Angle",
            [
                (0, 0),
                (.46, 20),
                (.58, -20),
                (.7, 20),
                (.82, -20),
                (.96, 0),
                (1.08, 25),
                (1.2, -25),
                (1.3, 25),
                (1.4, 0),
            ]
        );
        Frames(CarriedRecords, "Opacity", [(0, 1), (.86, 1), (.96, 0), (1.4, 0)]);
        Frames(CarriedMotion, "Y", [(0, 0), (.78, 0), (.96, 24), (1.4, 24)]);
        Frames(CarriedMotion, "X", [(0, 0), (.78, 0), (.96, 23), (1.4, 23)]);
        return storyboard;

        void Frames(
            DependencyObject target,
            string property,
            (double Seconds, double Value)[] frames,
            bool discrete = false
        ) => AddFrames(storyboard, target, property, frames, discrete);
    }
}
