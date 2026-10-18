using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Something.App;

public sealed partial class RecordLoadingAnimation
{
    private Storyboard CreateScanCycle()
    {
        var storyboard = new Storyboard();
        storyboard.Completed += OnCycleCompleted;
        // Walk in front of the DB, then pass behind its rim only after jumping above it.
        var depth = new ObjectAnimationUsingKeyFrames { EnableDependentAnimation = true };
        Storyboard.SetTarget(depth, Chick);
        Storyboard.SetTargetProperty(depth, "(Canvas.ZIndex)");
        depth.KeyFrames.Add(
            new DiscreteObjectKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 0 }
        );
        depth.KeyFrames.Add(
            new DiscreteObjectKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(.18)),
                Value = 1,
            }
        );
        depth.KeyFrames.Add(
            new DiscreteObjectKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(.96)),
                Value = 0,
            }
        );
        storyboard.Children.Add(depth);
        Animate(
            ChickMotion,
            "TranslateX",
            [(0, 0), (.18, 0), (.34, 46), (.5, 68), (.68, 84), (.84, 110), (1.02, 132), (1.3, 132)]
        );
        Animate(
            ChickMotion,
            "TranslateY",
            [
                (0, 45),
                (.18, -20),
                (.34, -8),
                (.5, 40),
                (.58, 37),
                (.68, 40),
                (.84, -32),
                (1.02, -38),
                (1.18, 30),
                (1.3, 50),
            ]
        );
        Animate(Chick, "Opacity", [(0, 0), (.12, 1), (1.18, 1), (1.3, 0)]);
        Animate(FrontSprite, "Opacity", [(0, 1), (.3, 0), (.86, 1), (1.3, 1)], discrete: true);
        Animate(SideSprite, "Opacity", [(0, 0), (.3, 1), (.86, 0), (1.3, 0)], discrete: true);
        Animate(
            LeftFootMotion,
            "Angle",
            [(0, 0), (.34, -25), (.46, 25), (.58, -25), (.7, 25), (.84, 0), (1.3, 0)]
        );
        Animate(
            RightFootMotion,
            "Angle",
            [(0, 0), (.34, 25), (.46, -25), (.58, 25), (.7, -25), (.84, 0), (1.3, 0)]
        );
        return storyboard;

        void Animate(
            DependencyObject target,
            string property,
            (double Seconds, double Value)[] frames,
            bool discrete = false
        )
        {
            var animation = new DoubleAnimationUsingKeyFrames();
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            foreach (var (seconds, value) in frames)
            {
                DoubleKeyFrame frame = discrete
                    ? new DiscreteDoubleKeyFrame()
                    : new LinearDoubleKeyFrame();
                frame.KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds));
                frame.Value = value;
                animation.KeyFrames.Add(frame);
            }
            storyboard.Children.Add(animation);
        }
    }
}
