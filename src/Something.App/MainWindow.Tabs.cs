using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Something.App;

public sealed partial class MainWindow
{
    private async Task VerifyTabPresentationAsync()
    {
        var originalTheme = RootLayout.RequestedTheme;
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                RootLayout.RequestedTheme = theme;
                foreach (var tabs in new[] { WorkspaceTabs })
                {
                    foreach (int selected in new[] { 2, 1, 0 })
                    {
                        tabs.SelectedIndex = selected;
                        if (tabs == WorkspaceTabs)
                            await RefreshManagementIfVisibleAsync();
                        RootLayout.UpdateLayout();
                        var tab = (TabViewItem)tabs.TabItems[selected];
                        var indicator =
                            FindTreePart<Border>(tab, "SelectionIndicator")
                            ?? throw new InvalidOperationException("Tab underline was not loaded.");
                        var content = FindTreePart<ContentPresenter>(tab, "ContentPresenter")!;
                        foreach (
                            string state in new[]
                            {
                                "Selected",
                                "PointerOverSelected",
                                "PressedSelected",
                            }
                        )
                        {
                            VisualStateManager.GoToState(tab, state, false);
                            RootLayout.UpdateLayout();
                            var origin = indicator
                                .TransformToVisual(tabs)
                                .TransformPoint(new(0, 0));
                            if (
                                indicator.Opacity != 1
                                || indicator.ActualHeight < 3
                                || origin.Y + indicator.ActualHeight > tabs.ActualHeight + 0.5
                                || content.FontWeight != Microsoft.UI.Text.FontWeights.SemiBold
                            )
                                throw new InvalidOperationException(
                                    $"Tab selection was hidden in {theme}/{state}."
                                );
                        }
                        VisualStateManager.GoToState(tab, "Selected", false);
                        foreach (
                            var other in tabs
                                .TabItems.Cast<TabViewItem>()
                                .Where(item => item != tab)
                        )
                            if (FindTreePart<Border>(other, "SelectionIndicator")?.Opacity != 0)
                                throw new InvalidOperationException(
                                    "An inactive tab retained its underline."
                                );
                    }
                }
            }
        }
        finally
        {
            WorkspaceTabs.SelectedIndex = 0;
            RootLayout.RequestedTheme = originalTheme;
        }
    }
}
