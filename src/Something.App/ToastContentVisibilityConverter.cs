using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Something.App;

public sealed class ToastContentVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is null || value is string text && string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
