using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Something.App;

/// <summary>The number input of the whole application (see docs/user-interface.md): desktop stepping
/// that also works on empty values. The wheel steps only while the box
/// has the focus, as do the up and down keys, so that scrolling a page past it does not change a
/// value by accident.</summary>
public sealed class SteppingNumberBox : NumberBox
{
    private long wheelRemainder;

    protected override void OnPreviewKeyDown(KeyRoutedEventArgs args)
    {
        // Like the wheel, the arrow keys step only while the box has the focus.
        if (args.Key is VirtualKey.Up or VirtualKey.Down && HasFocusWithin)
        {
            Step(args.Key == VirtualKey.Up ? 1 : -1);
            args.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(args);
    }

    protected override void OnPointerWheelChanged(PointerRoutedEventArgs args)
    {
        var properties = args.GetCurrentPoint(this).Properties;
        // Without the focus the wheel is left alone (not handled), so the page scrolls.
        if (!properties.IsHorizontalMouseWheel && properties.MouseWheelDelta != 0 && HasFocusWithin)
        {
            ApplyWheelDelta(properties.MouseWheelDelta);
            // Suppress NumberBox's own wheel handler to avoid stepping twice while focused.
            args.Handled = true;
            return;
        }
        base.OnPointerWheelChanged(args);
    }

    protected override void OnPointerExited(PointerRoutedEventArgs args)
    {
        wheelRemainder = 0;
        base.OnPointerExited(args);
    }

    protected override void OnLostFocus(RoutedEventArgs args)
    {
        wheelRemainder = 0;
        base.OnLostFocus(args);
    }

    /// <summary>Whether the box or its text field has the keyboard focus.</summary>
    internal bool HasFocusWithin
    {
        get
        {
            if (XamlRoot is null)
                return false;
            for (
                var node = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
                node is not null;
                node = VisualTreeHelper.GetParent(node)
            )
                if (ReferenceEquals(node, this))
                    return true;
            return false;
        }
    }

    internal void ApplyWheelDelta(int delta)
    {
        wheelRemainder += delta;
        long steps = wheelRemainder / 120;
        wheelRemainder %= 120;
        if (steps != 0)
            Step(steps);
    }

    /// <summary>Applies text that is typed but not yet committed (committing happens on Enter or
    /// when focus leaves), through NumberBox's own localized validation.</summary>
    internal void Commit()
    {
        if (GetTemplateChild("InputBox") is TextBox input && input.Text != Text)
            Text = input.Text;
    }

    internal void Step(long steps)
    {
        Commit();
        double current = double.IsNaN(Value) ? 0 : Value;
        Value = Math.Clamp(current + steps * SmallChange, Minimum, Maximum);
        if (GetTemplateChild("InputBox") is TextBox editor)
        {
            editor.SelectionStart = editor.Text.Length;
            editor.SelectionLength = 0;
        }
    }
}
