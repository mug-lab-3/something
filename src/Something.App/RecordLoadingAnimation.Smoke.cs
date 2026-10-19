namespace Something.App;

public sealed partial class RecordLoadingAnimation
{
    internal void PauseForPreview(double seconds)
    {
        if (!IsPlaying)
            return;
        activeCycle?.Pause();
        activeCycle?.Seek(TimeSpan.FromSeconds(seconds));
    }

    internal void ResumeAfterPreview()
    {
        if (IsPlaying)
            activeCycle?.Resume();
    }

    internal void VerifyOptimizationPose(double seconds)
    {
        if (
            ActiveKind != LoadingAnimationKind.OptimizeDatabase
            || Chick.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed
            || Width != 420
        )
            throw new InvalidOperationException(
                "Optimization did not use its full-size three-chick scene."
            );
        foreach (var (figure, motion) in OptimizationChicks)
            if (
                motion.ScaleX != 1
                || motion.ScaleY != 1
                || figure.Width != DefaultChick.Width
                || figure.Height != DefaultChick.Height
            )
                throw new InvalidOperationException(
                    "Optimization changed a chick size or aspect ratio."
                );
        if (
            seconds is > .3 and < .5
            && (
                OptimizeThird.Direction.ScaleX != -1
                || FirstTalk.Opacity <= 0
                || Math.Abs(OptimizeFirstMotion.TranslateY - OptimizeThirdMotion.TranslateY) < 5
            )
        )
            throw new InvalidOperationException(
                "Chicks did not chat in scattered positions before the cue."
            );
        if (
            seconds is > 1.1 and < 1.2
            && (
                OptimizationWhistle.Opacity < .9
                || ChickAttention.Opacity < .9
                || FirstTalk.Opacity != 0
                || SecondTalk.Opacity != 0
                || ThirdTalk.Opacity != 0
            )
        )
            throw new InvalidOperationException(
                "The visual whistle did not interrupt the conversation."
            );
        if (seconds is > 2.49 and < 2.51 && OptimizationWhistle.Opacity < .9)
            throw new InvalidOperationException(
                "The whistle disappeared before the chicks finished lining up."
            );
        if (seconds is > 2.85 and < 2.87)
        {
            foreach (var (figure, motion) in OptimizationChicks)
                if (
                    Math.Abs(motion.Rotation) > .01
                    || figure.Direction.ScaleX != 1
                    || figure.Side.Opacity != 1
                )
                    throw new InvalidOperationException(
                        "Chicks did not face the queue direction after the cue."
                    );
            if (
                Math.Abs(OptimizeSecondMotion.TranslateX - OptimizeFirstMotion.TranslateX - 62) > .1
                || Math.Abs(OptimizeThirdMotion.TranslateX - OptimizeSecondMotion.TranslateX - 62)
                    > .1
            )
                throw new InvalidOperationException("Chicks did not form an evenly spaced queue.");
        }
        if (
            seconds is > 3.84 and < 3.86
            && (
                OptimizeFirstMotion.TranslateY < -4
                || OptimizeSecondMotion.TranslateY >= -4
                || Math.Abs(OptimizeThirdMotion.TranslateX - 311) > .1
            )
        )
            throw new InvalidOperationException("Chicks did not enter the DB in queue order.");
        if (seconds > 4.75 && OptimizationChicks.Any(chick => chick.Figure.Opacity > .01))
            throw new InvalidOperationException(
                "A chick remained outside the DB at the cycle boundary."
            );
    }
}
