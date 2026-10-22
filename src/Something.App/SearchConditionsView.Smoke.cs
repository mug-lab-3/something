using Microsoft.UI.Xaml;

namespace Something.App;

public sealed partial class SearchConditionsView
{
    internal void TypeNameForSmoke(string text) => NameBox.Text = text;

    internal string RangePreviewForSmoke => RangePreviewText.Text;
    internal bool SearchEnabledForSmoke => SearchButton.IsEnabled;

    // Makes the range look as if the screen had been made some time ago.
    internal void AgeRangeForSmoke()
    {
        loading = true;
        RangeInput.Start = RangeInput.Start - TimeSpan.FromMinutes(10);
        RangeInput.End = RangeInput.End - TimeSpan.FromMinutes(10);
        loading = false;
        ReadControls();
    }

    // The size inputs are number boxes like the others in the application: they step by the
    // keyboard and wheel logic of SteppingNumberBox, and what they hold reaches the form.
    internal void VerifyNumberInputsForSmoke()
    {
        MinSizeBox.Value = 2;
        MaxSizeBox.Value = 1;
        if (
            Form.MinimumSize != "2"
            || Form.MaximumSize != "1"
            || SizeIssueText.Visibility != Visibility.Visible
            || SearchButton.IsEnabled
        )
            throw new InvalidOperationException("A reversed size range was not reported.");
        // Stepping from the empty value starts at zero, and the wheel steps once per notch.
        MaxSizeBox.Value = double.NaN;
        MaxSizeBox.Step(1);
        MaxSizeBox.ApplyWheelDelta(120);
        if (MaxSizeBox.Value != 2 || Form.MaximumSize != "2" || !SearchButton.IsEnabled)
            throw new InvalidOperationException("Stepping did not reach the form.");
        MinSizeBox.Step(-5);
        if (MinSizeBox.Value != 0)
            throw new InvalidOperationException("Stepping went below the minimum.");
        MinSizeBox.Value = 1.5;
        if (Form.MinimumSize != 1.5.ToString(System.Globalization.CultureInfo.CurrentCulture))
            throw new InvalidOperationException("A decimal size was not passed on.");
        MinSizeBox.Value = double.NaN;
        MaxSizeBox.Value = double.NaN;
        if (Form.MinimumSize.Length != 0 || Form.MaximumSize.Length != 0)
            throw new InvalidOperationException("An empty number was not passed on as empty.");
    }
}
