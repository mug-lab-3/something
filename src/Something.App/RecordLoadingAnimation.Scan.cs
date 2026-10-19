using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Something.App;

public sealed partial class RecordLoadingAnimation
{
    private const double ScanCycleSeconds = 3.8;

    private Storyboard CreateScanCycle()
    {
        var storyboard = new Storyboard();
        storyboard.Completed += OnCycleCompleted;
        // Lower into the disk, close around the full-size chick, lift, carry and release over the DB.
        Frames(
            CraneTravel,
            "X",
            [(0, 78), (1.7, 78), (2.45, 220), (3.3, 220), (ScanCycleSeconds, 78)]
        );
        Frames(
            CraneLift,
            "Y",
            [(0, 8), (.2, 8), (.75, 72), (1.05, 72), (1.6, 8), (ScanCycleSeconds, 8)]
        );
        Frames(
            CraneCable,
            "ScaleY",
            [(0, 4), (.2, 4), (.75, 68), (1.05, 68), (1.6, 4), (ScanCycleSeconds, 4)]
        );
        Frames(
            CraneLeftArm,
            "Angle",
            [(0, 25), (.75, 25), (.98, 0), (2.55, 0), (2.78, 25), (ScanCycleSeconds, 25)]
        );
        Frames(
            CraneRightArm,
            "Angle",
            [(0, -25), (.75, -25), (.98, 0), (2.55, 0), (2.78, -25), (ScanCycleSeconds, -25)]
        );
        Frames(
            ChickMotion,
            "TranslateX",
            [(0, -10), (1.7, -10), (2.45, 132), (ScanCycleSeconds, 132)]
        );
        Frames(
            ChickMotion,
            "TranslateY",
            [
                (0, 44),
                (1.05, 44),
                (1.6, -20),
                (2.75, -20),
                (2.9, -12),
                (3.08, 30),
                (3.25, 88),
                (ScanCycleSeconds, 88),
            ]
        );
        Frames(
            Chick,
            "Opacity",
            [(0, 0), (.98, 0), (1.16, 1), (3.08, 1), (3.25, 0), (ScanCycleSeconds, 0)]
        );
        Frames(
            ChickMotion,
            "Rotation",
            [(0, 0), (1.7, 0), (1.95, -3), (2.2, 3), (2.45, 0), (ScanCycleSeconds, 0)]
        );
        Frames(FrontSprite, "Opacity", [(0, 1), (ScanCycleSeconds, 1)], discrete: true);
        Frames(SideSprite, "Opacity", [(0, 0), (ScanCycleSeconds, 0)], discrete: true);
        Frames(
            LeftFootMotion,
            "Angle",
            [(0, 0), (1.3, -10), (1.6, 0), (2, 10), (2.45, 0), (ScanCycleSeconds, 0)]
        );
        Frames(
            RightFootMotion,
            "Angle",
            [(0, 0), (1.3, 10), (1.6, 0), (2, -10), (2.45, 0), (ScanCycleSeconds, 0)]
        );
        AddDepthFrames(storyboard, Chick, (0, 0), (1.25, 1), (2.98, 0));
        AddDepthFrames(storyboard, ScanCrane, (0, 0), (1.25, 2));
        return storyboard;

        void Frames(
            DependencyObject target,
            string property,
            (double Seconds, double Value)[] frames,
            bool discrete = false
        ) => AddFrames(storyboard, target, property, frames, discrete);
    }
}
