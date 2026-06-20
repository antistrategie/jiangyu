using System.Collections.Generic;

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
}
