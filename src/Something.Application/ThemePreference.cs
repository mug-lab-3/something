using System.Text.Json.Serialization;

namespace Something.Application;

/// <summary>User-selected appearance, independent of the UI framework.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThemePreference>))]
public enum ThemePreference
{
    System,
    Dark,
    Light,
}
