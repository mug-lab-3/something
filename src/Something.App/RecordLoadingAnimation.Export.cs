using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Something.App;

public sealed partial class RecordLoadingAnimation
{
    private const double ExportCycleSeconds = 2.3;

    // Where the chick stops beside the file, and where the sheet drops into it.
    internal const double ExportChickStop = 100;
    internal const double ExportSheetDrop = 148;

    private Storyboard CreateExportCycle()
    {
        var storyboard = new Storyboard();
        storyboard.Completed += OnCycleCompleted;
        // Peek out of the nest holding a sheet, hop to the file, toss it in and hop back.
        Frames(
            ChickMotion,
            "TranslateX",
            [
                (0, 0),
                (.45, 0),
                (.85, ExportChickStop),
                (1.4, ExportChickStop),
                (1.95, 0),
                (ExportCycleSeconds, 0),
            ]
        );
        Frames(
            ChickMotion,
            "TranslateY",
            [
                (0, 42),
                (.08, 20),
                (.4, 20),
                (.45, 28),
                (.62, -30),
                (.85, 22),
                (1.0, 22),
                (1.1, 12),
                (1.2, 22),
                (1.4, 22),
                (1.55, 4),
                (1.7, 22),
                (1.82, 8),
                (1.95, 22),
                (2.1, 42),
                (ExportCycleSeconds, 42),
            ]
        );
        Frames(
            ChickMotion,
            "Rotation",
            [(0, 0), (.4, 0), (.45, 6), (.55, -6), (.85, 0), (ExportCycleSeconds, 0)]
        );
        Frames(Chick, "Opacity", [(0, 0), (.04, 1), (2.1, 1), (2.12, 0), (ExportCycleSeconds, 0)]);
        // Face the file while hopping toward it, and turn to the viewer as it lands.
        Frames(
            FrontSprite,
            "Opacity",
            [(0, 1), (.45, 0), (.84, 1), (ExportCycleSeconds, 1)],
            discrete: true
        );
        Frames(
            SideSprite,
            "Opacity",
            [(0, 0), (.45, 1), (.84, 0), (ExportCycleSeconds, 0)],
            discrete: true
        );
        Frames(
            LeftFootMotion,
            "Angle",
            [
                (0, 0),
                (.45, -25),
                (.62, 15),
                (.85, 0),
                (1.55, -15),
                (1.82, 15),
                (1.95, 0),
                (ExportCycleSeconds, 0),
            ]
        );
        Frames(
            RightFootMotion,
            "Angle",
            [
                (0, 0),
                (.45, 25),
                (.62, -15),
                (.85, 0),
                (1.55, 15),
                (1.82, -15),
                (1.95, 0),
                (ExportCycleSeconds, 0),
            ]
        );
        // The sheet rides above the chick's head, then arcs into the file and disappears.
        Frames(
            ExportSheetMotion,
            "TranslateX",
            [
                (0, 0),
                (.45, 0),
                (.85, ExportChickStop),
                (1.05, ExportSheetDrop),
                (ExportCycleSeconds, ExportSheetDrop),
            ]
        );
        Frames(
            ExportSheetMotion,
            "TranslateY",
            [
                (0, 22),
                (.08, 0),
                (.4, 0),
                (.45, 8),
                (.62, -50),
                (.85, 2),
                (.95, -30),
                (1.05, -20),
                (1.25, 40),
                (ExportCycleSeconds, 40),
            ]
        );
        Frames(
            ExportSheetMotion,
            "Rotation",
            [(0, 0), (.85, 0), (1.05, 15), (1.25, 0), (ExportCycleSeconds, 0)]
        );
        Frames(
            ExportSheet,
            "Opacity",
            [(0, 0), (.08, 1), (1.2, 1), (1.3, 0), (ExportCycleSeconds, 0)]
        );
        // The file squashes and springs back as the sheet lands inside.
        Frames(
            ExportFileMotion,
            "ScaleY",
            [(0, 1), (1.22, 1), (1.3, .9), (1.42, 1.04), (1.5, 1), (ExportCycleSeconds, 1)]
        );
        Frames(
            ExportFileMotion,
            "ScaleX",
            [(0, 1), (1.22, 1), (1.3, 1.06), (1.42, .98), (1.5, 1), (ExportCycleSeconds, 1)]
        );
        // In the nest the chick stays behind the DB front; outside it walks in front of the file.
        AddDepthFrames(storyboard, Chick, (0, 0), (.45, 2), (1.95, 0));
        // The sheet goes behind the file front only while it drops in.
        AddDepthFrames(storyboard, ExportSheet, (0, 3), (1.05, 0));
        return storyboard;

        void Frames(
            DependencyObject target,
            string property,
            (double Seconds, double Value)[] frames,
            bool discrete = false
        ) => AddFrames(storyboard, target, property, frames, discrete);
    }
}
