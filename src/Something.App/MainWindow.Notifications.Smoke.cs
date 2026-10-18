using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Something.App;

public sealed partial class MainWindow
{
    // This preview hosts the same native controls, palette and style as both live toast hosts.
    private async Task<FrameworkElement> PreviewNotificationsAsync()
    {
        var preview = new Grid
        {
            Width = 1000,
            Height = 600,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        preview.ColumnDefinitions.Add(new());
        preview.ColumnDefinitions.Add(new());
        Grid.SetRowSpan(preview, 4);
        Canvas.SetZIndex(preview, 100);
        RootLayout.Children.Add(preview);
        foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
        {
            var panel = (Border)
                XamlReader.Load(
                    """
                    <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      Background="{ThemeResource ApplicationPageBackgroundThemeBrush}" Padding="28">
                      <StackPanel Spacing="20">
                        <TextBlock x:Name="Heading" FontSize="22" FontWeight="SemiBold"
                          Foreground="{ThemeResource TextFillColorPrimaryBrush}" />
                        <TextBlock Text="成功・情報はアクセント色、警告・エラーは意味色"
                          TextWrapping="Wrap" Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                        <StackPanel x:Name="Toasts" Spacing="16" />
                        <TextBlock Text="成功・情報は6秒で閉じます。警告・エラーは閉じるまで残ります。"
                          TextWrapping="Wrap" Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                      </StackPanel>
                    </Border>
                    """
                );
            panel.RequestedTheme = theme;
            Grid.SetColumn(panel, theme == ElementTheme.Dark ? 0 : 1);
            preview.Children.Add(panel);
            ((TextBlock)panel.FindName("Heading")).Text =
                theme == ElementTheme.Dark ? "ダーク" : "ライト";
            var toasts = (StackPanel)panel.FindName("Toasts");
            (InfoBarSeverity Severity, string Message)[] examples =
            [
                (InfoBarSeverity.Success, "選択した3件のスキャン履歴を削除しました。"),
                (InfoBarSeverity.Informational, "このファイルはすでに存在しません。"),
                (InfoBarSeverity.Warning, "ファイルが変更されたため、操作を中止しました。"),
                (InfoBarSeverity.Error, "ファイルを開けませんでした。アクセスが拒否されました。"),
            ];
            foreach (var (severity, message) in examples)
            {
                toasts.Children.Add(
                    new InfoBar
                    {
                        Style = ToastPresentation.GetStyle(severity),
                        Resources = new ResourceDictionary
                        {
                            Source = new("ms-appx:///Styles/ToastPalette.xaml"),
                        },
                        Title = ToastPresentation.GetTitle(severity),
                        Message = message,
                        Severity = severity,
                        IsOpen = true,
                        Margin = new(0),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Top,
                    }
                );
            }
        }
        ShowToast("トーストの表示確認", InfoBarSeverity.Success);
        RootLayout.UpdateLayout();
        await Task.Delay(200, lifetime.Token);
        foreach (var panel in preview.Children.Cast<Border>())
        {
            var bars = ((StackPanel)panel.FindName("Toasts")).Children.Cast<InfoBar>().ToArray();
            var backgrounds = bars.Select(bar =>
                    FindTreePart<Border>(bar, "ContentRoot")?.Background
                )
                .ToArray();
            if (backgrounds.Any(brush => brush is not SolidColorBrush))
                throw new InvalidOperationException(
                    "A toast severity did not render its background."
                );
            if (
                backgrounds
                    .Cast<SolidColorBrush>()
                    .Any(brush => brush.Color.A != 255 || brush.Opacity != 1)
            )
                throw new InvalidOperationException(
                    "Toast backgrounds must be opaque over the underlying list."
                );
            var success = (SolidColorBrush)backgrounds[0]!;
            var information = (SolidColorBrush)backgrounds[1]!;
            if (success.Color != information.Color || success.Opacity != information.Opacity)
                throw new InvalidOperationException(
                    "Success and information must share a neutral background."
                );
            if (
                bars[0].BorderBrush is not SolidColorBrush outline
                || outline.Color.A == 0
                || bars[1].BorderBrush is not SolidColorBrush neutralOutline
                || outline.Color == neutralOutline.Color
            )
                throw new InvalidOperationException("Success must have a distinct accent outline.");
            if (bars.Any(bar => FindTreePart<Button>(bar, "CloseButton") is null))
                throw new InvalidOperationException("A toast lost its native close button.");
            foreach (var bar in bars)
            {
                var title = FindTreePart<TextBlock>(bar, "Title")!;
                var message = FindTreePart<TextBlock>(bar, "Message")!;
                var titlePosition = title.TransformToVisual(bar).TransformPoint(default);
                var messagePosition = message.TransformToVisual(bar).TransformPoint(default);
                if (
                    Math.Abs(titlePosition.X - messagePosition.X) > 1
                    || messagePosition.Y < titlePosition.Y + title.ActualHeight
                )
                    throw new InvalidOperationException(
                        "Toast title and message must occupy separate aligned rows."
                    );
            }
            if (panel.ActualTheme == MessageBar.ActualTheme)
            {
                var live =
                    FindTreePart<Border>(MessageBar, "ContentRoot")?.Background as SolidColorBrush;
                if (live?.Color != success.Color || live.Opacity != success.Opacity)
                    throw new InvalidOperationException(
                        "The live toast differs from the preview palette."
                    );
            }
        }
        MessageBar.IsOpen = false;
        return preview;
    }
}
