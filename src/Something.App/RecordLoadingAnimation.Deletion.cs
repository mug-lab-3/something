using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Something.App;

public sealed partial class RecordLoadingAnimation
{
    private const double DeletionCycleSeconds = 3.1;

    private Storyboard CreateDeletionCycle()
    {
        var storyboard = new Storyboard();
        storyboard.Completed += OnCycleCompleted;
        // Emerge with a backpack, greet the viewer, then turn and walk completely off-screen.
        Frames(
            ChickMotion,
            "TranslateX",
            [(0, 0), (.18, 0), (.32, 42), (.56, 96), (2.28, 96), (DeletionCycleSeconds, 260)]
        );
        Frames(
            ChickMotion,
            "TranslateY",
            [
                (0, 35),
                (.18, -20),
                (.32, -38),
                (.56, 43),
                (.66, 40),
                (.76, 43),
                (2.28, 43),
                (2.4, 40),
                (2.52, 43),
                (2.64, 40),
                (2.76, 43),
                (2.88, 40),
                (3, 43),
                (DeletionCycleSeconds, 43),
            ]
        );
        Frames(
            ChickMotion,
            "Rotation",
            [
                (0, 0),
                (.85, 0),
                (1, -2),
                (1.2, 1),
                (1.4, -1),
                (1.65, 1),
                (1.9, 0),
                (DeletionCycleSeconds, 0),
            ]
        );
        Frames(Chick, "Opacity", [(0, 0), (.12, 1), (3.08, 1), (DeletionCycleSeconds, 0)]);
        Frames(
            FrontSprite,
            "Opacity",
            [(0, 1), (2.26, 0), (DeletionCycleSeconds, 0)],
            discrete: true
        );
        Frames(
            SideSprite,
            "Opacity",
            [(0, 0), (2.26, 1), (DeletionCycleSeconds, 1)],
            discrete: true
        );
        Frames(
            DefaultChick.RaisedWing,
            "Opacity",
            [(0, 0), (.8, 1), (2.26, 0), (DeletionCycleSeconds, 0)],
            discrete: true
        );
        Frames(
            DefaultChick.LoweredWing,
            "Opacity",
            [(0, 1), (.8, 0), (2.26, 1), (DeletionCycleSeconds, 1)],
            discrete: true
        );
        Frames(
            DefaultChick.WaveMotion,
            "Angle",
            [
                (0, 0),
                (.8, 0),
                (1, -135),
                (1.18, -148),
                (1.36, -128),
                (1.54, -146),
                (1.72, -130),
                (2.06, -135),
                (2.24, 0),
                (DeletionCycleSeconds, 0),
            ]
        );
        Frames(
            Farewell,
            "Opacity",
            [(0, 0), (.9, 0), (1, 1), (2.1, 1), (2.26, 0), (DeletionCycleSeconds, 0)]
        );
        Frames(
            LeftFootMotion,
            "Angle",
            [
                (0, 0),
                (2.28, 0),
                (2.4, -25),
                (2.52, 25),
                (2.64, -25),
                (2.76, 25),
                (2.88, -25),
                (3, 25),
                (DeletionCycleSeconds, 0),
            ]
        );
        Frames(
            RightFootMotion,
            "Angle",
            [
                (0, 0),
                (2.28, 0),
                (2.4, 25),
                (2.52, -25),
                (2.64, 25),
                (2.76, -25),
                (2.88, 25),
                (3, -25),
                (DeletionCycleSeconds, 0),
            ]
        );
        return storyboard;

        void Frames(
            DependencyObject target,
            string property,
            (double Seconds, double Value)[] frames,
            bool discrete = false
        ) => AddFrames(storyboard, target, property, frames, discrete);
    }
}
