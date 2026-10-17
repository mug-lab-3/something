using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Something.App;

internal static class ToastPresentation
{
    internal static Style GetStyle(InfoBarSeverity severity) =>
        (Style)
            Microsoft.UI.Xaml.Application.Current.Resources[
                severity == InfoBarSeverity.Success ? "SuccessToastStyle" : "ToastStyle"
            ];

    internal static string GetTitle(InfoBarSeverity severity) =>
        severity switch
        {
            InfoBarSeverity.Success => "完了しました",
            InfoBarSeverity.Informational => "お知らせ",
            InfoBarSeverity.Warning => "確認してください",
            InfoBarSeverity.Error => "処理できませんでした",
            _ => throw new ArgumentOutOfRangeException(nameof(severity)),
        };
}
