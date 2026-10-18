using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Something.App;

internal enum LoadingAnimationKind
{
    Inspect,
    DeleteHistory,
    OptimizeDatabase,
}

public sealed partial class RecordLoadingAnimation
{
    private readonly Dictionary<LoadingAnimationKind, Storyboard> maintenanceCycles = [];

    private void ApplyKind()
    {
        ActiveKind = requestedKind;
        bool inspect = ActiveKind == LoadingAnimationKind.Inspect;
        ScannerArch.Visibility =
            ScannerLamp.Visibility =
            GateReady.Visibility =
            ScanBeam.Visibility =
                inspect ? Visibility.Visible : Visibility.Collapsed;
        SortingBox.Visibility =
            ActiveKind == LoadingAnimationKind.DeleteHistory
                ? Visibility.Visible
                : Visibility.Collapsed;
        SortingStack.Visibility =
            ActiveKind == LoadingAnimationKind.OptimizeDatabase
                ? Visibility.Visible
                : Visibility.Collapsed;
        CarriedRecords.Opacity = 0;
    }

    private void BeginCycle()
    {
        activeCycle?.Stop();
        ApplyKind();
        if (ActiveKind == LoadingAnimationKind.Inspect)
            activeCycle = Cycle;
        else
        {
            if (!maintenanceCycles.TryGetValue(ActiveKind, out var storyboard))
            {
                storyboard = CreateMaintenanceCycle(ActiveKind);
                maintenanceCycles.Add(ActiveKind, storyboard);
            }
            activeCycle = storyboard;
        }
        activeCycle.Begin();
    }

    private Storyboard CreateMaintenanceCycle(LoadingAnimationKind kind)
    {
        var storyboard = new Storyboard();
        storyboard.Completed += OnCycleCompleted;
        bool deleting = kind == LoadingAnimationKind.DeleteHistory;
        // A complete task, including departure, is the unit of playback and settling.
        Animate(
            ChickMotion,
            "TranslateX",
            deleting
                ? [(0, 0), (.18, 0), (.46, 92), (.78, 136), (.96, 136), (1.4, 260)]
                : [(0, 94), (1.1, 94), (1.4, 260)]
        );
        Animate(
            ChickMotion,
            "TranslateY",
            deleting
                ?
                [
                    (0, 35),
                    (.18, -24),
                    (.3, -38),
                    (.46, 22),
                    (.6, 19),
                    (.7, 22),
                    (.78, 19),
                    (.88, 22),
                    (.96, 22),
                    (1.08, 19),
                    (1.2, 22),
                    (1.3, 19),
                    (1.4, 22),
                ]
                :
                [
                    (0, 22),
                    (.28, 20),
                    (.45, 22),
                    (.62, 19),
                    (.8, 22),
                    (1.1, 22),
                    (1.2, 19),
                    (1.3, 22),
                    (1.4, 22),
                ]
        );
        Animate(Chick, "Opacity", [(0, 0), (.12, 1), (1.39, 1), (1.4, 0)]);
        Animate(
            FrontSprite,
            "Opacity",
            [(0, 1), (deleting ? .4 : 1.08, 0), (1.4, 0)],
            discrete: true
        );
        Animate(
            SideSprite,
            "Opacity",
            [(0, 0), (deleting ? .4 : 1.08, 1), (1.4, 1)],
            discrete: true
        );
        Animate(
            LeftFootMotion,
            "Angle",
            [
                (0, 0),
                (.46, -20),
                (.58, 20),
                (.7, -20),
                (.82, 20),
                (.96, 0),
                (1.08, -25),
                (1.2, 25),
                (1.3, -25),
                (1.4, 0),
            ]
        );
        Animate(
            RightFootMotion,
            "Angle",
            [
                (0, 0),
                (.46, 20),
                (.58, -20),
                (.7, 20),
                (.82, -20),
                (.96, 0),
                (1.08, 25),
                (1.2, -25),
                (1.3, 25),
                (1.4, 0),
            ]
        );
        if (deleting)
        {
            Animate(CarriedRecords, "Opacity", [(0, 1), (.86, 1), (.96, 0), (1.4, 0)]);
            Animate(CarriedMotion, "Y", [(0, 0), (.78, 0), (.96, 24), (1.4, 24)]);
            Animate(CarriedMotion, "X", [(0, 0), (.78, 0), (.96, 23), (1.4, 23)]);
        }
        else
        {
            // Straighten the stack, lift it, then return it through the DB opening.
            Animate(StackMotion, "Rotation", [(0, -10), (.22, 6), (.42, 0), (1.4, 0)]);
            Animate(
                StackMotion,
                "TranslateX",
                [(0, 0), (.45, 0), (.72, -20), (1, -154), (1.4, -154)]
            );
            Animate(
                StackMotion,
                "TranslateY",
                [(0, 0), (.45, 0), (.72, -77), (.9, -77), (1.05, -43), (1.4, -43)]
            );
            Animate(SortingStack, "Opacity", [(0, 0), (.12, 1), (.9, 1), (1.05, 0), (1.4, 0)]);
            Animate(StackMotion, "ScaleX", [(0, 1), (.9, 1), (1.05, .6), (1.4, .6)]);
            Animate(StackMotion, "ScaleY", [(0, 1), (.9, 1), (1.05, .6), (1.4, .6)]);
        }
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
