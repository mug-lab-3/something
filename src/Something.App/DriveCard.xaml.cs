using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

    internal DriveCard(DriveChoice choice)
    {
        InitializeComponent();
        Choice = choice;
        DriveText.Text = choice.Drive;
        LabelText.Text = DisplayText.Escape(choice.Label);
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
            UsageText.Text =
                $"使用中 {DisplayText.Bytes(used)} / 合計 {DisplayText.Bytes((UInt128)current.Total.Value)}";
            FreeText.Text = $"空き {DisplayText.Bytes((UInt128)current.Free.Value)}";
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
        Width = selected ? 500 : 280;
        ScanDetails.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        CompactStateText.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
        SelectedIcon.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        CardButton.BorderBrush = (Brush)
            Microsoft.UI.Xaml.Application.Current.Resources[
                selected ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"
            ];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            CardButton,
            $"{Choice.Drive} {Choice.Label}{(selected ? " 選択中" : "")}"
        );
    }

    internal void SetResult(ScanSession? result)
    {
        FilesText.Text =
            $"ファイル数 {result?.RootSample.Entries?.SubtreeFileCount.ToString("N0") ?? "—"}";
        SetState(
            result is null ? "未スキャン" : $"スキャン完了 · {result.Elapsed.TotalSeconds:F1}秒"
        );
    }

    internal void SetState(string text)
    {
        ScanStateText.Text = text;
        CompactStateText.Text = text;
    }

    internal void SetProgress(bool scanning, double? fraction = null)
    {
        ScanProgressBar.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        ScanProgressBar.IsIndeterminate = scanning && fraction is null;
        if (fraction is { } value)
            ScanProgressBar.Value = value * 100;
    }
}
