namespace Something.App;

public sealed partial class RecordLoadingAnimation
{
    internal void VerifyInspectPose(double seconds)
    {
        if (
            ActiveKind != LoadingAnimationKind.Inspect
            || ChickMotion.ScaleX != 1
            || ChickMotion.ScaleY != 1
        )
            throw new InvalidOperationException(
                "Loading did not preserve the shared full-size chick."
            );
        if (
            seconds is >= .2 and <= .67
            && (
                Math.Abs(ChickMotion.TranslateY - 20) > .1
                || Chick.Opacity < .99
                || Math.Abs(ChickMotion.TranslateX) > .1
            )
        )
            throw new InvalidOperationException("The chick did not pause to peek over the DB rim.");
        if (
            (
                seconds is .2 or .33
                && (DefaultChick.Gaze.X > -4.9 || Math.Abs(ChickMotion.Rotation + 2) > .1)
            )
            || (
                seconds is .54 or .67
                && (DefaultChick.Gaze.X < 4.9 || Math.Abs(ChickMotion.Rotation - 2) > .1)
            )
            || (seconds >= .98 && Math.Abs(DefaultChick.Gaze.X) > .1)
        )
            throw new InvalidOperationException(
                "The chick did not look left, right and then forward before jumping."
            );
        if (
            seconds == .84
            && (
                ChickMotion.TranslateY < 27
                || LeftFootMotion.Angle > -15
                || RightFootMotion.Angle < 15
            )
        )
            throw new InvalidOperationException("The chick did not crouch before jumping.");
        if (seconds == .98 && (ChickMotion.TranslateY > -37 || ChickMotion.TranslateX <= 0))
            throw new InvalidOperationException(
                "The chick did not leap forward after pushing off."
            );
        if (
            seconds == 1.22
            && (
                Math.Abs(ChickMotion.TranslateX - 136) > .1
                || Math.Abs(ChickMotion.TranslateY - 22) > .1
                || FrontSprite.Opacity != 1
                || ScanBeam.Opacity <= 0
            )
        )
            throw new InvalidOperationException("The chick did not land facing the scanner.");
        if (seconds == 1.52 && (SideSprite.Opacity != 1 || ScanBeam.Opacity != 0))
            throw new InvalidOperationException("The chick departed before the scan finished.");
    }

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
        // Presentations end wherever the cycle is; settling hides the whole scene instead.
        if (Target.Opacity != 0)
            throw new InvalidOperationException("The animation remained visible after settling.");
        if (LastPresentedFor > TimeSpan.Zero && LastPresentedFor < MinimumVisible)
            throw new InvalidOperationException("A shown animation ended before its minimum time.");
    }

    internal void PauseForPreview(double seconds)
    {
        if (!IsPresented)
            return;
        activeCycle?.Pause();
        activeCycle?.Seek(
            TimeSpan.FromSeconds(
                ActiveKind == LoadingAnimationKind.Inspect
                    ? seconds / InspectPlaybackSpeedRatio
                    : seconds
            )
        );
    }

    internal void ResumeAfterPreview()
    {
        if (IsPresented)
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

    internal void VerifyExportPose(double seconds)
    {
        if (
            ActiveKind != LoadingAnimationKind.Export
            || ExportTruck.Visibility != Microsoft.UI.Xaml.Visibility.Visible
            || ExportBox.Visibility != Microsoft.UI.Xaml.Visibility.Visible
            || Width != ExportWidth
            || ChickMotion.ScaleX != 1
            || ChickMotion.ScaleY != 1
            || Depth(ExportBox) <= Depth(ExportTruck)
        )
            throw new InvalidOperationException(
                "Export did not show the shared full-size chick, its box and the truck."
            );
        // Canvas positions of the box and the truck bed (the bed is the truck's first 76 px).
        double boxLeft = 67 + ExportBoxMotion.TranslateX;
        double boxBottom = 30 + ExportBoxMotion.TranslateY + 30;
        double bedLeft = 250 + ExportTruckMotion.TranslateX;
        double bedTop = 47 + 24 * 1.5 + ExportTruckMotion.TranslateY;
        static bool Near(double actual, double expected) => Math.Abs(actual - expected) <= .5;
        if (
            seconds is > .19 and < .21
            && (
                !Near(ChickMotion.TranslateY, 20)
                || ExportBox.Opacity < .99
                || !Near(ExportBoxMotion.TranslateX, ChickMotion.TranslateX)
                || !Near(ExportBoxMotion.TranslateY, ChickMotion.TranslateY - 20)
                || Depth(Chick) != 0
                || !Near(ExportTruckMotion.TranslateX, 0)
            )
        )
            throw new InvalidOperationException(
                "The chick did not peek out of the nest with a box on its head beside the already parked truck."
            );
        if (
            seconds is > .94 and < .96
            && (
                !Near(ChickMotion.TranslateX, ExportChickStop)
                || !Near(ChickMotion.TranslateY, 22)
                || !Near(ExportBoxMotion.TranslateX, ExportChickStop)
                || SideSprite.Opacity != 0
                || !Near(ExportTruckMotion.TranslateX, 0)
                || Depth(Chick) <= Depth(ExportTruck)
                || Depth(ExportBox) <= Depth(Chick)
                || 61 + ChickMotion.TranslateX + Chick.Width + 12 > bedLeft - 16 * 1.5
            )
        )
            throw new InvalidOperationException(
                $"The chick did not stop beside the parked truck with the box on its head: x={ChickMotion.TranslateX}, y={ChickMotion.TranslateY}, box={ExportBoxMotion.TranslateX}, side={SideSprite.Opacity}, truck={ExportTruckMotion.TranslateX}."
            );
        if (
            seconds is > 1.29 and < 1.31
            && (
                boxLeft < bedLeft
                || boxLeft + 42 > bedLeft + 40 * 1.5 - 8
                || !Near(boxBottom, bedTop)
                || !Near(ExportTruckMotion.TranslateX, 0)
            )
        )
            throw new InvalidOperationException(
                $"The box did not land on the truck bed: box=({boxLeft}, {boxBottom}), bed=({bedLeft}, {bedTop})."
            );
        if (
            seconds is > 1.79 and < 1.81
            && (ExportExhaust.Opacity < .5 || ExportTruckMotion.TranslateX <= 0)
        )
            throw new InvalidOperationException(
                "The truck did not puff exhaust as it pulled away."
            );
        if (
            seconds is > 2.09 and < 2.11
            && (
                ExportTruckMotion.TranslateX <= 0
                || !Near(ExportBoxMotion.TranslateX - ExportBoxOnBedX, ExportTruckMotion.TranslateX)
                || !Near(ChickMotion.TranslateX, ExportChickStop)
            )
        )
            throw new InvalidOperationException(
                "The box did not leave with the truck while the chick watched."
            );
        if (
            seconds >= 3.2
            && (
                Chick.Opacity != 0
                || ExportBox.Opacity != 0
                || !Near(ChickMotion.TranslateX, 0)
                || bedLeft < Width
            )
        )
            throw new InvalidOperationException(
                "The chick did not return into the nest after the truck drove off the canvas."
            );
    }

    internal void VerifyStaticExportPose()
    {
        if (
            Chick.Opacity != 1
            || ExportBox.Opacity != 1
            || ExportTruckMotion.TranslateX != 0
            || Math.Abs(ChickMotion.TranslateX - ExportChickStop) > .1
            || Math.Abs(ExportBoxMotion.TranslateX - ExportChickStop) > .1
            || Depth(ExportBox) <= Depth(Chick)
        )
            throw new InvalidOperationException(
                "Reduced-motion export did not show the chick holding a box beside the truck."
            );
    }

    private static int Depth(Microsoft.UI.Xaml.UIElement element) =>
        Microsoft.UI.Xaml.Controls.Canvas.GetZIndex(element);

    internal void VerifyDepartureAccessoriesHidden()
    {
        if (
            (
                ActiveKind != LoadingAnimationKind.ScanDisk
                && ScanCrane.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed
            )
            || (
                ActiveKind != LoadingAnimationKind.Export
                && (
                    ExportTruck.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed
                    || ExportBox.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed
                    || Microsoft.UI.Xaml.Controls.Canvas.GetZIndex(Chick) > 1
                )
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
