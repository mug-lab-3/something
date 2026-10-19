namespace Something.App;

public sealed partial class RecordLoadingAnimation
{
    internal void VerifyScanPose(double seconds)
    {
        if (
            ActiveKind != LoadingAnimationKind.ScanDisk
            || ScanCrane.Visibility != Microsoft.UI.Xaml.Visibility.Visible
            || ChickMotion.ScaleX != 1
            || ChickMotion.ScaleY != 1
            || SideSprite.Opacity != 0
        )
            throw new InvalidOperationException(
                "Crane scan did not preserve the full-size front-facing chick."
            );
        if (
            seconds is >= 1.6 and <= 2.45
            && (
                Math.Abs(61 + ChickMotion.TranslateX + 27 - CraneTravel.X) > .1
                || Math.Abs(38 + ChickMotion.TranslateY - CraneLift.Y - 10) > .1
                || CraneLeftArm.Angle != 0
                || CraneRightArm.Angle != 0
                || Chick.Opacity < .99
            )
        )
            throw new InvalidOperationException(
                "The closed crane did not carry the chick with it."
            );
        if (
            seconds == 2.8
            && (
                CraneLeftArm.Angle < 24
                || CraneRightArm.Angle > -24
                || CraneTravel.X != 220
                || ChickMotion.TranslateY <= -20
            )
        )
            throw new InvalidOperationException("The crane did not release the chick over the DB.");
        if (seconds >= 3.7 && Chick.Opacity != 0)
            throw new InvalidOperationException("The chick did not finish falling into the DB.");
    }

    internal void VerifySettledPose()
    {
        if (IsPlaying)
            throw new InvalidOperationException("Animation is still playing after settling.");
        if (ActiveKind == LoadingAnimationKind.OptimizeDatabase)
            VerifyOptimizationPose(4.9);
        else if (Chick.Opacity != 0 || Farewell.Opacity != 0)
            throw new InvalidOperationException("A chick or farewell reappeared after settling.");
        if (
            ActiveKind == LoadingAnimationKind.Inspect
            && (ScanBeam.Opacity != 0 || GateReady.Opacity != 0)
        )
            throw new InvalidOperationException("Scanner effects remained visible after settling.");
    }

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

    internal void VerifyDeletionPose(double seconds)
    {
        if (
            ActiveKind != LoadingAnimationKind.DeleteHistory
            || !DefaultChick.HasBackpack
            || Width != 300
            || ChickMotion.ScaleX != 1
            || ChickMotion.ScaleY != 1
        )
            throw new InvalidOperationException(
                "Farewell did not preserve the shared full-size chick with a backpack."
            );
        if (
            seconds is > 1.49 and < 1.51
            && (
                DefaultChick.RaisedWing.Opacity != 1
                || DefaultChick.WaveMotion.Angle > -120
                || FrontSprite.Opacity != 1
                || Farewell.Opacity < .9
                || Math.Abs(ChickMotion.TranslateX - 96) > .1
            )
        )
            throw new InvalidOperationException(
                "The chick did not stop, raise its wing and greet the viewer."
            );
        if (
            seconds is > 2.49 and < 2.51
            && (
                SideSprite.Opacity != 1
                || Farewell.Opacity != 0
                || ChickMotion.TranslateX <= 96
                || DefaultChick.RaisedWing.Opacity != 0
            )
        )
            throw new InvalidOperationException(
                "The departing chick did not turn and walk away after greeting."
            );
        if (seconds > 3.07 && 61 + ChickMotion.TranslateX - 16 <= Width)
            throw new InvalidOperationException(
                "The chick or backpack reset before leaving the canvas."
            );
    }

    internal void VerifyDepartureAccessoriesHidden()
    {
        if (
            (
                ActiveKind != LoadingAnimationKind.ScanDisk
                && ScanCrane.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed
            )
            || DefaultChick.HasBackpack
            || DefaultChick.RaisedWing.Opacity != 0
            || Farewell.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed
            || OptimizationChicks.Any(chick => chick.Figure.HasBackpack)
        )
            throw new InvalidOperationException(
                "Farewell accessories leaked into another animation mode."
            );
    }
}
