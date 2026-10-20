using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Something.App;

/// <summary>
/// Display text from Strings/ja-JP/Resources.resw. XAML uses x:Uid; code uses these keys.
/// A missing key fails loudly instead of showing a blank control.
/// </summary>
internal static class Strings
{
    private static readonly ResourceLoader Loader = new();

    public static string Get(string key) =>
        Loader.GetString(key) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Missing display text resource: {key}");

    public static string Format(string key, params ReadOnlySpan<object?> arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
}
