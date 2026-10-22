using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Something.App;

/// <summary>Desktop stepping that also works on empty values and while hovering without focus.</summary>
public sealed class SteppingNumberBox : NumberBox
{
    private long wheelRemainder;

    protected override void OnPreviewKeyDown(KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Up or VirtualKey.Down)
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
        if (!properties.IsHorizontalMouseWheel && properties.MouseWheelDelta != 0)
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
