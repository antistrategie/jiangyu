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

    /// <summary>Install the active locale's UI string table. Called by the loader at load. Mods do
    /// not call this.</summary>
    public static void Install(IReadOnlyDictionary<string, string> strings)
        => _strings = strings ?? new Dictionary<string, string>();

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
