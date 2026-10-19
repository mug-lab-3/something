using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Something.App;

public sealed partial class ChickFigure : UserControl
{
    public ChickFigure() => InitializeComponent();

    internal FrameworkElement Front => FrontSprite;
    internal FrameworkElement Side => SideSprite;
    internal RotateTransform LeftFoot => LeftFootMotion;
    internal RotateTransform RightFoot => RightFootMotion;
    internal ScaleTransform Direction => SideDirection;

    internal void FaceRight(bool right)
    {
        FrontSprite.Opacity = right ? 0 : 1;
        SideSprite.Opacity = right ? 1 : 0;
    }
}
