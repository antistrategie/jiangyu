using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jiangyu.Sdk;

/// <summary>
/// Active-language lookup for a mod's code and UXML strings. A mod calls <see cref="Text"/> with a
/// stable key and an English fallback. The loader installs the active locale's table at load. When
/// no translation is installed (the source language, or an untranslated key) the fallback is
/// returned, so a string is always shown.
///
/// <para>The key is namespaced by mod id, matching the msgctxt a translator fills in the PO:
/// <c>Locale.Text("WOMENACE::ui/swap_form", "SWAP FORM")</c>. Template and KDL text is localised
/// automatically and needs no call here. Named <c>Locale</c> rather than <c>Loca</c> to avoid a
/// clash with the game's own <c>Il2CppMenace.Tools.Loca</c>.</para>
/// </summary>
public static class Locale
{
    private static IReadOnlyDictionary<string, string> _strings = new Dictionary<string, string>();

    // The game's own strings a mod puts its text in place of, by the game's key.
    private static readonly Dictionary<string, LocalisedText> _replacements = new(StringComparer.Ordinal);

    /// <summary>Raised with the game key when a mod replaces a game string, so the loader writes it
    /// in at once when the game's text is already loaded.</summary>
    internal static Action<string> ReplacementsChanged;

    /// <summary>Install the active locale's UI string table. Called by the loader at load. Mods do
    /// not call this.</summary>
    public static void Install(IReadOnlyDictionary<string, string> strings)
        => _strings = strings ?? new Dictionary<string, string>();

    /// <summary>
    /// Show <paramref name="text"/> wherever the game shows its own string under
    /// <paramref name="gameKey"/>, the key the game's localisation table files it under
    /// (<c>UI/common/squaddies</c>). Use it for the game's interface text, which belongs to no
    /// template a KDL patch could reach. A template's text is patched in KDL instead.
    ///
    /// <para>The replacement is translated like any other mod string: declare it as
    /// <c>new LocalisedText(key, fallback)</c> in the call so it reaches the POT. In the source
    /// language the fallback is shown. In any other language the mod's translation is shown, and
    /// where the mod ships none the game's own translation is kept, so a player never sees English
    /// in place of their language. The game reloads its table when the language changes, and the
    /// loader writes the replacements in again each time. A later call for the same key wins.</para>
    ///
    /// <para>A key the game has no string under is reported once in the log and changes nothing.
    /// Where the game formats its string, the replacement may use its numbered placeholders
    /// (<c>{0}</c>) up to the highest and leave some out. One that would not format with the values
    /// the game passes (a higher number, a stray brace) is reported and the game's own text stays,
    /// in that language. A string the game does not format, such as an event text with its own
    /// bracketed tokens, takes any text.</para>
    ///
    /// <para>Called from the game's main thread, the replacement shows at once. Called from another
    /// thread, it shows from the next scene load or language change.</para>
    /// </summary>
    public static void Replace(string gameKey, LocalisedText text)
    {
        if (string.IsNullOrEmpty(gameKey) || !text.HasText)
            return;
        lock (_replacements)
            _replacements[gameKey] = text;
        ReplacementsChanged?.Invoke(gameKey);
    }

    /// <summary>The replacements for the active language: each game key with its text, or a null
    /// text where the active translation does not cover the key (never in the
    /// <paramref name="sourceLanguage"/>). <paramref name="keys"/> narrows the result to those
    /// keys. Null returns every replacement.</summary>
    internal static List<(string Key, string Text)> Replacements(bool sourceLanguage, IEnumerable<string> keys = null)
    {
        var strings = _strings;
        var result = new List<(string Key, string Text)>();
        lock (_replacements)
        {
            foreach (var key in keys ?? _replacements.Keys)
            {
                if (!_replacements.TryGetValue(key, out var text))
                    continue;
                if (sourceLanguage)
                    result.Add((key, text.Fallback));
                else if (text.Key != null && strings.TryGetValue(text.Key, out var translated) && !string.IsNullOrEmpty(translated))
                    result.Add((key, translated));
                else
                    result.Add((key, null));
            }
        }
        return result;
    }

    /// <summary>The active-language string for <paramref name="key"/>, or
    /// <paramref name="fallback"/> when none is installed.</summary>
    public static string Text(string key, string fallback)
        => key != null && _strings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : fallback;

    /// <summary>As <see cref="Text"/>, with <paramref name="args"/> substituted into the result.
    ///
    /// <para>Use this rather than <c>string.Format(Locale.Text(...), ...)</c>. A placeholder is
    /// part of the string a translator receives, so a PO entry can drop <c>{0}</c>, add an index
    /// the caller never passes, or leave a brace unclosed, and raw <c>string.Format</c> throws
    /// <c>FormatException</c> on all three. These strings are built inside coroutines and UI
    /// builders, where a throw does not merely lose one label: it abandons the routine and leaves
    /// the screen it was drawing half-built. A translation that cannot take the arguments falls
    /// back to <paramref name="fallback"/>, which the mod author wrote, so the line reads English
    /// but everything around it keeps running. Extracted to the POT exactly as
    /// <see cref="Text"/> is.</para>
    ///
    /// <para>Formats invariantly rather than in the machine's culture. The language shown is the
    /// one the mod's locale table selects, which has nothing to do with the OS regional setting,
    /// so inheriting it would print a German decimal comma into otherwise English UI purely
    /// because of where the player lives.</para></summary>
    public static string Format(string key, string fallback, params object[] args)
    {
        var text = Text(key, fallback);
        // Catches everything, not just FormatException: a null args array raises
        // ArgumentNullException and an argument whose ToString faults raises whatever it likes.
        // The whole point of this method is that a caller can never be taken down by one.
        try { return string.Format(CultureInfo.InvariantCulture, text, args); }
        catch (Exception) { }

        try { return string.Format(CultureInfo.InvariantCulture, fallback, args); }
        catch (Exception) { return fallback; }
    }
}
