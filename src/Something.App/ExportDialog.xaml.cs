using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Something.Application.Export;

namespace Something.App;

internal enum ExportScope
{
    Displayed,
    FolderSummary,
}

/// <summary>Folder summary limits; null means no limit.</summary>
internal sealed record ExportOptions(
    ExportScope Scope,
    ExportFormat Format,
    int? MaximumDepth,
    UInt128 MinimumBytes,
    int? Limit,
    bool SortByChange
);

public sealed partial class ExportDialog : ContentDialog
{
    internal ExportDialog(string? displayedDescription, string summaryStart, bool comparing)
    {
        InitializeComponent();
        DisplayedOption.IsEnabled = displayedDescription is not null;
        DisplayedDescription.Text =
            displayedDescription ?? Strings.Get("Export_DisplayedUnavailable");
        SummaryStartText.Text = Strings.Format("Export_SummaryStart", summaryStart);
        SortByChangeItem.IsEnabled = comparing;
        ComparisonNote.Visibility = comparing ? Visibility.Visible : Visibility.Collapsed;
        SortBox.SelectedIndex = 0;
        FormatBox.SelectedIndex = (int)ExportFormat.Csv;
        ScopeChoice.SelectedIndex = displayedDescription is null ? 1 : 0;
        UpdateScope();
    }

    internal ExportOptions Options =>
        new(
            ScopeChoice.SelectedIndex == 1 ? ExportScope.FolderSummary : ExportScope.Displayed,
            (ExportFormat)FormatBox.SelectedIndex,
            double.IsNaN(DepthBox.Value) ? null : (int)DepthBox.Value,
            double.IsNaN(MinimumSizeBox.Value)
                ? UInt128.Zero
                : (UInt128)Math.Round(MinimumSizeBox.Value * 1024 * 1024),
            double.IsNaN(LimitBox.Value) ? null : (int)LimitBox.Value,
            SortBox.SelectedIndex == 1
        );

    private void OnScopeChanged(object sender, SelectionChangedEventArgs args) => UpdateScope();

    private void UpdateScope()
    {
        if (SummaryOptions is null)
            return;
        bool summary = ScopeChoice.SelectedIndex == 1;
        SummaryOptions.Visibility = summary ? Visibility.Visible : Visibility.Collapsed;
        DisplayedDescription.Visibility = summary ? Visibility.Collapsed : Visibility.Visible;
    }
}
