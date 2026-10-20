using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Something.App;

public sealed partial class RecordLoadingAnimation
{
    private const double ExportCycleSeconds = 3.4;
    internal const double ExportWidth = 440;

    // The chick stops beside the truck bed; the box then rests in the middle of the bed.
    internal const double ExportChickStop = 96;
    internal const double ExportBoxOnBedX = 187;
    internal const double ExportBoxOnBedY = 23;

    // Leave enough room for the enlarged truck and its exhaust to exit the canvas.
    private const double ExportTruckGone = 220;

    private Storyboard CreateExportCycle()
    {
        var storyboard = new Storyboard();
        storyboard.Completed += OnCycleCompleted;
        // The loaded truck is already parked; the chick adds one box before it pulls away.
        Frames(
            ExportTruckMotion,
            "TranslateX",
            [(0, 0), (1.75, 0), (2.45, ExportTruckGone), (ExportCycleSeconds, ExportTruckGone)]
        );
        // The bed dips under the box's weight.
        Frames(
            ExportTruckMotion,
            "TranslateY",
            [(0, 0), (1.3, 0), (1.36, 2), (1.5, 0), (ExportCycleSeconds, 0)]
        );
        Frames(
            ExportExhaust,
            "Opacity",
            [(0, 0), (1.7, 0), (1.8, .8), (2.1, 0), (ExportCycleSeconds, 0)]
        );
        Frames(
            ChickMotion,
            "TranslateX",
            [
                (0, 0),
                (.5, 0),
                (.95, ExportChickStop),
                (2.4, ExportChickStop),
                (2.95, 0),
                (ExportCycleSeconds, 0),
            ]
        );
        Frames(
            ChickMotion,
            "TranslateY",
            [
                (0, 42),
                (.08, 20),
                (.45, 20),
                (.5, 28),
                (.7, -8),
                (.95, 22),
                (1.1, 14),
                (1.3, 22),
                (2.4, 22),
                (2.55, 4),
                (2.7, 22),
                (2.82, 8),
                (2.95, 22),
                (3.1, 42),
                (ExportCycleSeconds, 42),
            ]
        );
        Frames(
            ChickMotion,
            "Rotation",
            [(0, 0), (.45, 0), (.5, 6), (.6, -6), (.95, 0), (ExportCycleSeconds, 0)]
        );
        Frames(Chick, "Opacity", [(0, 0), (.04, 1), (3.1, 1), (3.12, 0), (ExportCycleSeconds, 0)]);
        // Face the truck while hopping toward it, and turn to the viewer as it lands.
        Frames(
            FrontSprite,
            "Opacity",
            [(0, 1), (.5, 0), (.94, 1), (ExportCycleSeconds, 1)],
            discrete: true
        );
        Frames(
            SideSprite,
            "Opacity",
            [(0, 0), (.5, 1), (.94, 0), (ExportCycleSeconds, 0)],
            discrete: true
        );
        Frames(
            LeftFootMotion,
            "Angle",
            [
                (0, 0),
                (.5, -25),
                (.7, 15),
                (.95, 0),
                (2.55, -15),
                (2.82, 15),
                (2.95, 0),
                (ExportCycleSeconds, 0),
            ]
        );
        Frames(
            RightFootMotion,
            "Angle",
            [
                (0, 0),
                (.5, 25),
                (.7, -15),
                (.95, 0),
                (2.55, 15),
                (2.82, -15),
                (2.95, 0),
                (ExportCycleSeconds, 0),
            ]
        );
        // The box rides on the chick's head, is lifted onto the bed, then leaves with the truck.
        Frames(
            ExportBoxMotion,
            "TranslateX",
            [
                (0, 0),
                (.5, 0),
                (.95, ExportChickStop),
                (1.1, 142),
                (1.3, ExportBoxOnBedX),
                (1.75, ExportBoxOnBedX),
                (2.45, ExportBoxOnBedX + ExportTruckGone),
                (ExportCycleSeconds, ExportBoxOnBedX + ExportTruckGone),
            ]
        );
        Frames(
            ExportBoxMotion,
            "TranslateY",
            [
                (0, 22),
                (.08, 0),
                (.45, 0),
                (.5, 8),
                (.7, -28),
                (.95, 2),
                (1.1, -6),
                (1.3, ExportBoxOnBedY),
                (1.36, ExportBoxOnBedY + 2),
                (1.5, ExportBoxOnBedY),
                (ExportCycleSeconds, ExportBoxOnBedY),
            ]
        );
        Frames(
            ExportBox,
            "Opacity",
            [(0, 0), (.08, 1), (2.45, 1), (2.46, 0), (ExportCycleSeconds, 0)]
        );
        // In the nest the chick stays behind the DB front; outside it stands in front of the truck.
        AddDepthFrames(storyboard, Chick, (0, 0), (.5, 2), (2.95, 0));
        return storyboard;

        void Frames(
            DependencyObject target,
            string property,
            (double Seconds, double Value)[] frames,
            bool discrete = false
        ) => AddFrames(storyboard, target, property, frames, discrete);
    }
}
