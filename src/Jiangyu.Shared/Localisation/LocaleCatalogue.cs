using System;
using System.Collections.Generic;

namespace Jiangyu.Shared.Localisation;

/// <summary>
/// The canonical map between MENACE's <c>LocaLanguage</c> enum members and the locale codes a
/// mod's translation files are named with. Shared so the compiler (which sees only the code on a
/// <c>locales/&lt;code&gt;.po</c> filename) and the loader (which reads the live
/// <c>LocaLanguage</c> by name) agree on the same set without either referencing the other.
///
/// English is the source language: a mod authors its strings in English via
/// <c>m_DefaultTranslation</c>, so there is no <c>en</c> translation file.
/// </summary>
public static class LocaleCatalogue
{
    /// <summary>The source language's <c>LocaLanguage</c> member name.</summary>
    public const string SourceLanguageName = "English";

    /// <summary>The source language's locale code.</summary>
    public const string SourceLanguageCode = "en";

    /// <summary>
    /// <c>LocaLanguage</c> member name to locale code, for every non-source language. The loader
    /// looks up by <c>LocaManager.GetCurrentLanguage().ToString()</c>. The compiler validates a
    /// <c>locales/&lt;code&gt;.po</c> filename against the values.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> CodesByLanguageName =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["German"] = "de",
            ["French"] = "fr",
            ["Polish"] = "pl",
            ["SpanishSpain"] = "es_ES",
            ["PortugueseBrazil"] = "pt_BR",
            ["Russian"] = "ru",
            ["Japanese"] = "ja",
            ["Korean"] = "ko",
            ["ChineseSimplified"] = "zh_Hans",
            ["ChineseTraditional"] = "zh_Hant",
            ["Turkish"] = "tr",
            ["Ukrainian"] = "uk",
        };

    /// <summary>The locale code for a live <c>LocaLanguage</c> member name, or null when it is the
    /// source language or an unknown member.</summary>
    public static string? CodeForLanguageName(string languageName)
    {
        if (string.IsNullOrEmpty(languageName) || languageName == SourceLanguageName)
            return null;
        return CodesByLanguageName.TryGetValue(languageName, out var code) ? code : null;
    }

    private static readonly HashSet<string> KnownCodes = new(CodesByLanguageName.Values, StringComparer.Ordinal);

    /// <summary>True when <paramref name="code"/> names a translatable (non-source) locale.</summary>
    public static bool IsKnownCode(string code) => !string.IsNullOrEmpty(code) && KnownCodes.Contains(code);
}

/// <summary>
/// File and directory names for the localisation pipeline, shared between the compiler that
/// writes them and the loader that reads them.
/// </summary>
public static class LocaleLayout
{
    /// <summary>The folder holding the mod's <c>&lt;code&gt;.po</c> files, both as authored in the mod
    /// root and as staged under <c>compiled/locales/</c> for the loader to read.</summary>
    public const string SourceDirName = "locales";

    /// <summary>The compiled source catalogue for a mod: <c>&lt;modName&gt;.pot</c>.</summary>
    public static string CatalogueFileName(string modName) => modName + ".pot";
}
