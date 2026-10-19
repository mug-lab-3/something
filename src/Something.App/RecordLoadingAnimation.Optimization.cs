using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Something.App;

public sealed partial class RecordLoadingAnimation
{
    private const double OptimizationTimelineEndSeconds = 3.9;
    private const double OptimizationCueSeconds = 1.05;

    private (ChickFigure Figure, CompositeTransform Motion)[] OptimizationChicks =>
        [
            (OptimizeFirst, OptimizeFirstMotion),
            (OptimizeSecond, OptimizeSecondMotion),
            (OptimizeThird, OptimizeThirdMotion),
        ];

    private void SetOptimizationPose()
    {
        FirstTalk.Opacity =
            SecondTalk.Opacity =
            ThirdTalk.Opacity =
            ChickAttention.Opacity =
            OptimizationWhistle.Opacity =
                0;
        int index = 0;
        foreach (var (figure, motion) in OptimizationChicks)
        {
            figure.Opacity = 1;
            figure.FaceRight(true);
            figure.Direction.ScaleX = 1;
            Canvas.SetZIndex(figure, 1);
            motion.TranslateX = 102 + 62 * index++;
            motion.TranslateY = 0;
            motion.ScaleX = motion.ScaleY = 1;
            motion.Rotation = 0;
        }
    }

    private Storyboard CreateOptimizationCycle()
    {
        var storyboard = new Storyboard();
        storyboard.Completed += OnCycleCompleted;
        // Visual whistle only: no audio playback or sound assets.
        Frames(
            FirstTalk,
            "Opacity",
            [
                (0, 0),
                (.1, 1),
                (.38, 1),
                (.5, 0),
                (.7, 1),
                (.92, 1),
                (1.02, 0),
                (OptimizationTimelineEndSeconds, 0),
            ]
        );
        Frames(
            SecondTalk,
            "Opacity",
            [
                (0, 0),
                (.28, 0),
                (.4, 1),
                (.65, 1),
                (.78, 0),
                (1.02, 0),
                (OptimizationTimelineEndSeconds, 0),
            ]
        );
        Frames(
            ThirdTalk,
            "Opacity",
            [(0, 0), (.55, 0), (.7, 1), (.92, 1), (1.02, 0), (OptimizationTimelineEndSeconds, 0)]
        );
        Frames(
            OptimizationWhistle,
            "Opacity",
            [
                (0, 0),
                (.98, 0),
                (OptimizationCueSeconds, 1),
                (2.05, 1),
                (2.15, 0),
                (OptimizationTimelineEndSeconds, 0),
            ]
        );
        Frames(
            WhistleMotion,
            "Rotation",
            [
                (0, 0),
                (.98, 0),
                (1.05, -10),
                (1.12, 8),
                (1.19, -8),
                (1.26, 5),
                (1.35, 0),
                (OptimizationTimelineEndSeconds, 0),
            ]
        );
        Frames(
            ChickAttention,
            "Opacity",
            [
                (0, 0),
                (OptimizationCueSeconds, 1),
                (1.27, 1),
                (1.4, 0),
                (OptimizationTimelineEndSeconds, 0),
            ],
            discrete: true
        );
        int index = 0;
        foreach (var (figure, motion) in OptimizationChicks)
        {
            double scattered = index switch
            {
                0 => 18,
                1 => 104,
                _ => 202,
            };
            double ground = index switch
            {
                0 => 8,
                1 => -26,
                _ => -12,
            };
            double tilt = index == 1 ? 5 : -5;
            double aligned = 102 + 62 * index;
            double turn = 1.3 + index * .12;
            double hop = 2.3 + (2 - index) * .4;
            double walkMiddle = (2.15 + hop) / 2;
            // Talk first. The cue is followed by a visible reaction, then an orderly queue.
            Frames(
                motion,
                "TranslateX",
                [
                    (0, scattered),
                    (.32, scattered - 3),
                    (.6, scattered + 3),
                    (.92, scattered),
                    (1.3, scattered),
                    (2.05, aligned),
                    (2.15, aligned),
                    (hop, 226),
                    (hop + .27, 311),
                    (OptimizationTimelineEndSeconds, 311),
                ]
            );
            Frames(
                motion,
                "TranslateY",
                [
                    (0, ground),
                    (.22, ground - 2),
                    (.48, ground),
                    (.72, ground - 2),
                    (.98, ground),
                    (1.1, ground - 6),
                    (1.26, ground),
                    (1.3, ground),
                    (1.5, -3),
                    (1.7, 0),
                    (1.9, -3),
                    (2.05, 0),
                    (2.15, 0),
                    (walkMiddle, -3),
                    (hop, 0),
                    (hop + .26, -68),
                    (hop + .35, -66),
                    (hop + .58, 12),
                    (OptimizationTimelineEndSeconds, 18),
                ]
            );
            Frames(
                motion,
                "Rotation",
                [
                    (0, tilt),
                    (.25, -tilt),
                    (.5, tilt),
                    (.75, -tilt),
                    (.98, tilt),
                    (1.1, 0),
                    (OptimizationTimelineEndSeconds, 0),
                ]
            );
            Frames(
                figure,
                "Opacity",
                [
                    (0, 0),
                    (.12, 1),
                    (hop + .48, 1),
                    (hop + .63, 0),
                    (OptimizationTimelineEndSeconds, 0),
                ]
            );
            Frames(
                figure.Front,
                "Opacity",
                [
                    (0, index == 1 ? 1 : 0),
                    (OptimizationCueSeconds, 1),
                    (turn, 0),
                    (hop + .27, 1),
                    (OptimizationTimelineEndSeconds, 1),
                ],
                discrete: true
            );
            Frames(
                figure.Side,
                "Opacity",
                [
                    (0, index == 1 ? 0 : 1),
                    (OptimizationCueSeconds, 0),
                    (turn, 1),
                    (hop + .27, 0),
                    (OptimizationTimelineEndSeconds, 0),
                ],
                discrete: true
            );
            Frames(
                figure.Direction,
                "ScaleX",
                [(0, index == 2 ? -1 : 1), (turn, 1), (OptimizationTimelineEndSeconds, 1)],
                discrete: true
            );
            Frames(
                figure.LeftFoot,
                "Angle",
                [
                    (0, 0),
                    (1.3, 0),
                    (1.45, -20),
                    (1.6, 20),
                    (1.75, -20),
                    (1.9, 20),
                    (2.05, 0),
                    (2.15, 0),
                    (walkMiddle, -20),
                    (hop, 0),
                    (OptimizationTimelineEndSeconds, 0),
                ]
            );
            Frames(
                figure.RightFoot,
                "Angle",
                [
                    (0, 0),
                    (1.3, 0),
                    (1.45, 20),
                    (1.6, -20),
                    (1.75, 20),
                    (1.9, -20),
                    (2.05, 0),
                    (2.15, 0),
                    (walkMiddle, 20),
                    (hop, 0),
                    (OptimizationTimelineEndSeconds, 0),
                ]
            );
            AddDepthFrames(storyboard, figure, (0, 1), (OptimizationTime(hop + .27), 0));
            index++;
        }
        return storyboard;

        void Frames(
            DependencyObject target,
            string property,
            (double Seconds, double Value)[] frames,
            bool discrete = false
        ) =>
            AddFrames(
                storyboard,
                target,
                property,
                frames.Select(frame => (OptimizationTime(frame.Seconds), frame.Value)).ToArray(),
                discrete
            );
    }

    // Keep walking/jumping speeds, but give the cue, reaction and completed queue time to read.
    // The 3.9-second sequence becomes 4.9 seconds; the whistle stays visible for about 2 seconds.
    private static double OptimizationTime(double seconds) =>
        seconds switch
        {
            <= OptimizationCueSeconds => seconds,
            <= 1.3 => OptimizationCueSeconds + (seconds - OptimizationCueSeconds) * 2.8,
            <= 2.05 => 1.75 + (seconds - 1.3) * (4.0 / 3),
            <= 2.3 => 2.75 + (seconds - 2.05) * 2.2,
            _ => seconds + 1,
        };
}
