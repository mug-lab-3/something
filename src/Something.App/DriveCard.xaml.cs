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

    internal void SetBusy(bool busy, bool selected)
    {
        CardButton.IsEnabled = !busy;
        ScanIconButton.IsEnabled = !busy || selected;
        bool cancel = busy && selected;
        ScanIcon.Glyph = cancel ? "\uE71A" : "\uE72C";
        string label = cancel ? "スキャンをキャンセル" : "このドライブをスキャン";
        ToolTipService.SetToolTip(ScanIconButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ScanIconButton,
            $"{Choice.Drive} {label}"
        );
    }

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
            FreeText.Text = $"空き {DisplayText.Bytes((UInt128)current.Free.Value)}";
            ToolTipService.SetToolTip(
                UsageText,
                $"使用中 {DisplayText.Bytes(used)} / 合計 {DisplayText.Bytes((UInt128)current.Total.Value)}"
            );
        }
        else
        {
            UsageBar.Value = 0;
            UsageText.Text = "ドライブ容量を取得できません";
            FreeText.Text = "空き容量 不明";
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
            $"{Choice.Drive} {Choice.Label}{(selected ? " 選択中" : "")}"
        );
    }

    internal void SetResult(ScanSession? result)
    {
        ScanStateText.Text = result is null
            ? "未スキャン"
            : $"✓ スキャン済み · {result.RootSample.Entries?.SubtreeFileCount.ToString("N0") ?? "—"}件";
        ToolTipService.SetToolTip(
            ScanStateText,
            result is null ? "未スキャン" : $"スキャン完了 · {result.Elapsed.TotalSeconds:F1}秒"
        );
    }

    internal void SetState(string text)
    {
        ScanStateText.Text = text;
        ToolTipService.SetToolTip(ScanStatus, text);
    }

    internal void SetProgress(bool scanning)
    {
        ScanSpinner.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        ScanSpinner.IsActive = scanning;
    }
}
