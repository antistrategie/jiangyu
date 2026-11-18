using System.Globalization;
using System.Text.RegularExpressions;

namespace Jiangyu.Loader.Runtime.Localisation;

/// <summary>
/// Whether a replacement for one of the game's strings can stand where the game's own text did.
/// The game fills a string's numbered placeholders with <c>string.Format</c>, which throws inside
/// the game's code on a placeholder past the values it passes, on a format a number cannot take,
/// or on a stray brace.
/// </summary>
internal static class GameTextFormat
{
    private static readonly Regex Placeholder = new(@"\{(\d+)[^{}]*\}", RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="text"/> formats with the values the game's
    /// <paramref name="original"/> takes. A replacement may leave a value out. The text is tried
    /// with numbers, the values most of the game's strings take and the ones a format can fail on.
    /// A string without placeholders is tried with none, since the game may still format it. One
    /// that does not format itself is not formatted by the game.</summary>
    public static bool Fits(string text, string original)
    {
        if (text == null)
            return false;
        var values = 0;
        // An escaped brace in the game's string is a literal, not a placeholder.
        var unescaped = (original ?? "").Replace("{{", "", StringComparison.Ordinal).Replace("}}", "", StringComparison.Ordinal);
        foreach (Match match in Placeholder.Matches(unescaped))
            if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                values = Math.Max(values, index + 1);
        var probe = new object[values];
        for (var i = 0; i < values; i++)
            probe[i] = 1;
        // A string of the game's own that does not format (the event and mission texts' bracketed
        // tokens) is not one the game formats, so any text stands in for it.
        return !Formats(original ?? "", probe) || Formats(text, probe);
    }

    private static bool Formats(string text, object[] values)
    {
        try
        {
            _ = string.Format(CultureInfo.InvariantCulture, text, values);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
