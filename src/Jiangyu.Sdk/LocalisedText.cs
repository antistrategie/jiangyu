namespace Jiangyu.Sdk;

/// <summary>
/// A translatable string declared as DATA: a (key, English fallback) pair you store in a table or a
/// field and resolve at display time with <see cref="Resolve"/>. A runtime <c>Locale.Text(key, fallback)</c>
/// call with computed arguments cannot be seen by the compile-time POT extractor, but a literal
/// <c>new LocalisedText("key", "English")</c> is, so data-driven UI strings still reach translators.
/// Keep both arguments string literals, or the extractor will skip the entry.
/// </summary>
public readonly struct LocalisedText
{
    public readonly string Key;
    public readonly string Fallback;

    public LocalisedText(string key, string fallback)
    {
        Key = key;
        Fallback = fallback;
    }

    /// <summary>True when this carries authored text (an entry left default does not).</summary>
    public bool HasText => !string.IsNullOrEmpty(Fallback);

    /// <summary>The string for the current language, falling back to the authored English.</summary>
    public string Resolve() => Locale.Text(Key, Fallback);

    public override string ToString() => Resolve();
}
