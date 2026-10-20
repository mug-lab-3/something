using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Something.Storage;

namespace Something.App;

public sealed partial class HistoryDeleteDialog : ContentDialog
{
    internal bool Optimize => OptimizeAfterDelete.IsChecked == true;

    internal HistoryDeleteDialog(ScanHistoryEntry[] rows)
    {
        InitializeComponent();
        Title = $"{rows.Length:N0} 件のスキャン履歴を削除しますか？";
        RangeText.Text =
            $"{rows.Min(row => row.CompletedUtc).ToLocalTime():yyyy/MM/dd HH:mm:ss} – {rows.Max(row => row.CompletedUtc).ToLocalTime():yyyy/MM/dd HH:mm:ss}";
    }

    internal void VerifyDefaultOptions()
    {
        if (Optimize || DefaultButton != ContentDialogButton.None)
            throw new InvalidOperationException(
                "Delete confirmation must not default to deletion or optimization."
            );
    }

    internal void ConfirmSynthetic(bool optimize)
    {
        OptimizeAfterDelete.IsChecked = optimize;
        var button =
            FindButton(this, "PrimaryButton")
            ?? throw new InvalidOperationException(
                "Delete confirmation primary button was not loaded."
            );
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    internal async Task VerifyButtonStatesAsync(CancellationToken token)
    {
        var button =
            FindButton(this, "PrimaryButton")
            ?? throw new InvalidOperationException("Delete button was not loaded.");
        var content =
            VisualTreeHelper.GetChild(button, 0) as ContentPresenter
            ?? throw new InvalidOperationException("Delete button template was not loaded.");
        var originalTheme = RequestedTheme;
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                RequestedTheme = theme;
                var colors = new HashSet<(global::Windows.UI.Color Color, double Opacity)>();
                foreach (string state in new[] { "Normal", "PointerOver", "Pressed" })
                {
                    if (!VisualStateManager.GoToState(button, state, false))
                        throw new InvalidOperationException(
                            $"Missing delete button state: {state}."
                        );
                    await Task.Delay(120, token);
                    if (
                        content.Background is not SolidColorBrush background
                        || content.Foreground is not SolidColorBrush foreground
                        || background.Color.A == 0
                        || foreground.Color.A == 0
                        || button.Style.BasedOn
                            != (Style)
                                Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"]
                    )
                        throw new InvalidOperationException(
                            $"Confirmation button lost its native accent styling in {theme}/{state}."
                        );
                    colors.Add((background.Color, background.Opacity));
                }
                if (colors.Count != 3)
                    throw new InvalidOperationException(
                        "Delete button states lack visual feedback."
                    );
            }
        }
        finally
        {
            RequestedTheme = originalTheme;
            VisualStateManager.GoToState(button, "Normal", false);
        }
    }

    private static Button? FindButton(DependencyObject parent, string name)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Button primary && primary.Name == name)
                return primary;
            if (FindButton(child, name) is { } nested)
                return nested;
        }
        return null;
    }
}
