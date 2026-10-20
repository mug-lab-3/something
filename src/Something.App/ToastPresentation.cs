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
            InfoBarSeverity.Success => Strings.Get("Toast_Success"),
            InfoBarSeverity.Informational => Strings.Get("Toast_Information"),
            InfoBarSeverity.Warning => Strings.Get("Toast_Warning"),
            InfoBarSeverity.Error => Strings.Get("Toast_Error"),
            _ => throw new ArgumentOutOfRangeException(nameof(severity)),
        };
}
