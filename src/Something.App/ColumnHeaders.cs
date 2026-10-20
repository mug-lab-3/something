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
            selected && descending ? Strings.Get("Sort_Ascending")
            : selected ? Strings.Get("Sort_Descending")
            : Strings.Get("Sort_Action");
        AutomationProperties.SetName(
            button,
            selected
                ? $"{label} · {(descending ? Strings.Get("Sort_Descending") : Strings.Get("Sort_Ascending"))}"
                : label
        );
        AutomationProperties.SetHelpText(button, Strings.Format("Sort_Help", label, next));
    }
}
