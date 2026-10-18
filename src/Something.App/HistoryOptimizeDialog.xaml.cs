using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Something.App;

public sealed partial class HistoryOptimizeDialog : ContentDialog
{
    public HistoryOptimizeDialog() => InitializeComponent();

    internal void ConfirmSynthetic()
    {
        var button =
            GetTemplateChild("PrimaryButton") as Button
            ?? throw new InvalidOperationException(
                "Optimization confirmation primary button was not loaded."
            );
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }
}
