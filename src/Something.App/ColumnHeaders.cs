using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Something.App;

internal static class ColumnHeaders
{
    internal static void Update(Button button, string label, bool selected, bool descending)
    {
        button.Content =
            label
            + (
                selected
                    ? descending
                        ? " ▼"
                        : " ▲"
                    : ""
            );
        string next =
            selected && descending ? "昇順"
            : selected ? "降順"
            : "並べ替え";
        AutomationProperties.SetName(
            button,
            selected ? $"{label} · {(descending ? "降順" : "昇順")}" : label
        );
        AutomationProperties.SetHelpText(button, $"クリックして{label}で{next}");
    }
}
