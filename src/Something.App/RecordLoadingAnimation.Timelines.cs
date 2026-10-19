using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Something.App;

public sealed partial class RecordLoadingAnimation
{
    private static void AddFrames(
        Storyboard storyboard,
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

    private static void AddDepthFrames(
        Storyboard storyboard,
        DependencyObject target,
        params (double Seconds, int Depth)[] frames
    )
    {
        var animation = new ObjectAnimationUsingKeyFrames { EnableDependentAnimation = true };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "(Canvas.ZIndex)");
        foreach (var (seconds, depth) in frames)
            animation.KeyFrames.Add(
                new DiscreteObjectKeyFrame
                {
                    KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)),
                    Value = depth,
                }
            );
        storyboard.Children.Add(animation);
    }
}
