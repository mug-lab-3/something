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
        Title = $"{rows.Length:N0}件のスキャン履歴を削除しますか？";
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
