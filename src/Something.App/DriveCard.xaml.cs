using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Something.Application;

namespace Something.App;

internal sealed record DriveChoice(
    string Drive,
    string Label,
    long? Total,
    long? Free,
    bool IsSynthetic = false
);

public sealed partial class DriveCard : UserControl
{
    internal DriveChoice Choice { get; }
    internal string StateDescription =>
        ToolTipService.GetToolTip(ScanStateText) as string ?? ScanStateText.Text;
    internal event EventHandler? Invoked;
    internal event EventHandler? ScanRequested;
    private bool? selectedState;

    internal DriveCard(DriveChoice choice)
    {
        InitializeComponent();
        Choice = choice;
        DriveText.Text = choice.Drive;
        LabelText.Text = DisplayText.Escape(choice.Label);
        ToolTipService.SetToolTip(
            LabelText,
            $"{choice.Drive} {DisplayText.Escape(choice.Label)} · NTFS"
        );
        RefreshVolume();
    }

    private void OnInvoked(object sender, RoutedEventArgs args) =>
        Invoked?.Invoke(this, EventArgs.Empty);

    private void OnScanRequested(object sender, RoutedEventArgs args) =>
        ScanRequested?.Invoke(this, EventArgs.Empty);

    internal void SetBusy(bool busy)
    {
        CardButton.IsEnabled = true;
        ScanIconButton.IsEnabled = true;
        bool cancel = busy;
        ScanIcon.Glyph = cancel ? "\uE71A" : "\uE72C";
        string label = cancel ? Strings.Get("DriveCard_CancelScan") : Strings.Get("DriveCard_Scan");
        ToolTipService.SetToolTip(ScanIconButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ScanIconButton,
            $"{Choice.Drive} {label}"
        );
    }

    internal void SetDemoNextStage(DemoScanStage stage) =>
        ToolTipService.SetToolTip(
            ScanIconButton,
            Strings.Format("DriveCard_NextDemoScan", Choice.Drive, StatusText.Describe(stage))
        );

    internal void RefreshVolume()
    {
        var current = Choice;
        if (!current.IsSynthetic)
        {
            try
            {
                var info = new DriveInfo(current.Drive);
                current = current with { Total = info.TotalSize, Free = info.TotalFreeSpace };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                current = current with { Total = null, Free = null };
            }
        }
        if (current.Total is > 0 && current.Free is >= 0 && current.Free <= current.Total)
        {
            var used = checked((UInt128)(current.Total.Value - current.Free.Value));
            UsageBar.Value = (double)used / current.Total.Value * 100;
            UsageText.Text = DisplayText.CapacityRatio(used, (UInt128)current.Total.Value);
            FreeText.Text = Strings.Format(
                "DriveCard_Free",
                DisplayText.Bytes((UInt128)current.Free.Value)
            );
            ToolTipService.SetToolTip(
                UsageText,
                Strings.Format(
                    "DriveCard_Usage",
                    DisplayText.Bytes(used),
                    DisplayText.Bytes((UInt128)current.Total.Value)
                )
            );
        }
        else
        {
            UsageBar.Value = 0;
            UsageText.Text = Strings.Get("DriveCard_CapacityUnavailable");
            FreeText.Text = Strings.Get("DriveCard_FreeUnknown");
        }
    }

    internal void SetSelected(bool selected)
    {
        if (selectedState == selected)
            return;
        selectedState = selected;
        // This outline is outside Button's visual states, so PointerOver/Pressed
        // cannot replace it. Selection does not change the card's background.
        SelectionOutline.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            CardButton,
            $"{Choice.Drive} {Choice.Label}{(selected ? Strings.Get("DriveCard_SelectedSuffix") : "")}"
        );
    }

    internal void SetResult(ScanSession? result)
    {
        ScanStateText.Text = result is null
            ? Strings.Get("Drive_NotScanned")
            : Strings.Format(
                "DriveCard_LastScan",
                result.CompletedLocal?.ToString("yyyy/MM/dd HH:mm")
                    ?? Strings.Get("DriveCard_UnknownTime")
            );
        ToolTipService.SetToolTip(
            ScanStateText,
            result is null
                ? Strings.Get("Drive_NotScanned")
                : Strings.Format(
                    "DriveCard_ScanDetails",
                    result.CompletedLocal?.ToString("yyyy/MM/dd HH:mm:ss")
                        ?? Strings.Get("DriveCard_UnknownTime"),
                    result.Elapsed.TotalSeconds,
                    result.RootSample.Entries?.SubtreeFileCount.ToString("N0") ?? "—",
                    result.MetadataUnresolved
                )
        );
        ToolTipService.SetToolTip(ScanStatus, ToolTipService.GetToolTip(ScanStateText));
    }

    internal void SetState(string text, string? details = null)
    {
        ScanStateText.Text = text;
        ToolTipService.SetToolTip(ScanStatus, details ?? text);
        ToolTipService.SetToolTip(ScanStateText, details ?? text);
    }

    internal void SetProgress(bool scanning)
    {
        ScanSpinner.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        ScanSpinner.IsActive = scanning;
    }
}
