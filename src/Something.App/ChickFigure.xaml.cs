using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Something.App;

public sealed partial class ChickFigure : UserControl
{
    public ChickFigure() => InitializeComponent();

    internal FrameworkElement Front => FrontSprite;
    internal FrameworkElement Side => SideSprite;
    internal TranslateTransform Gaze => FrontFaceMotion;
    internal RotateTransform LeftFoot => LeftFootMotion;
    internal RotateTransform RightFoot => RightFootMotion;
    internal ScaleTransform Direction => SideDirection;
    internal FrameworkElement RaisedWing => GreetingWing;
    internal FrameworkElement LoweredWing => RestingRightWing;
    internal RotateTransform WaveMotion => GreetingWingMotion;
    internal bool HasBackpack => FrontBackpack.Visibility == Visibility.Visible;

    internal void WearBackpack(bool value) =>
        FrontBackpack.Visibility =
            FrontStraps.Visibility =
            SideBackpack.Visibility =
                value ? Visibility.Visible : Visibility.Collapsed;

    internal void SetFarewellPose(bool greeting)
    {
        GreetingWing.Opacity = greeting ? 1 : 0;
        RestingRightWing.Opacity = greeting ? 0 : 1;
        GreetingWingMotion.Angle = greeting ? -135 : 0;
    }

    internal void FaceRight(bool right)
    {
        FrontSprite.Opacity = right ? 0 : 1;
        SideSprite.Opacity = right ? 1 : 0;
    }
}
