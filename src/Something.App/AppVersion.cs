using System.Reflection;

namespace Something.App;

internal static class AppVersion
{
    // The SDK generates this metadata from Version in Something.App.csproj.
    // Keep prerelease labels, but omit the optional build/commit suffix in the UI.
    internal static string Display { get; } =
        typeof(App)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];

    internal static string WindowTitle => $"Something {Display}";
}
