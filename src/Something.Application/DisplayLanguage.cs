using System.Globalization;
using System.Text.Json.Serialization;

namespace Something.Application;

/// <summary>User-selected display language, independent of the UI framework.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LanguagePreference>))]
public enum LanguagePreference
{
    System,
    Japanese,
    English,
}

/// <summary>
/// Chooses the display language. The application ships Japanese and English text; any system
/// language other than Japanese uses English.
/// </summary>
public static class DisplayLanguage
{
    public const string Japanese = "ja-JP";
    public const string English = "en-US";

    /// <summary>Command-line option that selects the language of a demo, which keeps no settings.</summary>
    public const string Option = "--language";

    public static string Resolve(LanguagePreference preference, CultureInfo system)
    {
        ArgumentNullException.ThrowIfNull(system);
        return preference switch
        {
            LanguagePreference.Japanese => Japanese,
            LanguagePreference.English => English,
            LanguagePreference.System => system.TwoLetterISOLanguageName == "ja"
                ? Japanese
                : English,
            _ => throw new ArgumentOutOfRangeException(nameof(preference)),
        };
    }

    /// <summary>The preference given by <see cref="Option"/>, or null when it is absent.</summary>
    public static LanguagePreference? FromArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        int index = IndexOfOption(arguments);
        if (index < 0)
            return null;
        return index + 1 < arguments.Count
            ? arguments[index + 1] switch
            {
                Japanese => LanguagePreference.Japanese,
                English => LanguagePreference.English,
                "system" => LanguagePreference.System,
                var value => throw new ArgumentException(
                    $"Unsupported display language: {value}.",
                    nameof(arguments)
                ),
            }
            : throw new ArgumentException("The language option needs a value.", nameof(arguments));
    }

    /// <summary>
    /// Arguments for restarting in another language. A demo carries the choice on its command
    /// line because it keeps no settings; a normal start reads it from the saved settings.
    /// </summary>
    public static IReadOnlyList<string> RestartArguments(
        IReadOnlyList<string> arguments,
        bool demo,
        LanguagePreference preference
    )
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var kept = new List<string>(arguments);
        for (int index = IndexOfOption(kept); index >= 0; index = IndexOfOption(kept))
            kept.RemoveRange(index, Math.Min(2, kept.Count - index));
        if (demo)
            kept.AddRange([
                Option,
                preference switch
                {
                    LanguagePreference.Japanese => Japanese,
                    LanguagePreference.English => English,
                    LanguagePreference.System => "system",
                    _ => throw new ArgumentOutOfRangeException(nameof(preference)),
                },
            ]);
        return kept;
    }

    private static int IndexOfOption(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count; index++)
            if (arguments[index] == Option)
                return index;
        return -1;
    }
}
