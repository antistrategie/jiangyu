using Jiangyu.Loader.Logging;
using MelonLoader;
using LocaCategory = Il2CppMenace.Tools.LocaCategory;
using LocaManager = Il2CppMenace.Tools.LocaManager;

namespace Jiangyu.Loader.Runtime.Localisation;

/// <summary>
/// Writes the game strings mods replace (<see cref="Jiangyu.Sdk.Locale.Replace"/>) into the game's
/// loca store, in the entry under the game's own key. The key's first segment is its category, as
/// the game files it. A key the game has no entry for is reported once and left alone in every
/// language: a mod replaces the game's text, it does not add keys. A replacement that would not
/// format with the values the game passes its string (<see cref="GameTextFormat"/>), which would
/// otherwise throw inside the game's code, is reported and the game's text stays in that
/// language.
///
/// <para>The game rebuilds the store from its CSV on every language change, so this runs after
/// each locale apply and switch. A full write happens for a new language or a rebuilt store, and
/// otherwise only the keys replaced since the last write are written. A replacement made on the
/// main thread once a language has been applied is written at once. One made on another thread
/// waits for the next scene load or language change. In the source language both the entry's
/// default and its translation are written, as the template injector does. In another language
/// only the translation is, and only where the mod translated it. A key the active translation
/// does not cover gets the game's own text back, so an earlier replacement for it does not
/// stay.</para>
/// </summary>
internal static class GameTextReplacer
{
    // What one entry held before this wrote it, and what this wrote, so a later write can tell
    // a rebuilt store from its own text and put the game's text back.
    private sealed class Written
    {
        public string Text;
        public string OriginalTranslation;
        public string OriginalDefault;
        public bool WroteDefault;
    }

    /// <summary>The replacements the store holds while the source language is active, by full key,
    /// which the game's code reads past the store (<see cref="GameTextTranslatePatch"/>), and the
    /// last segment of each key, the one check every Translate call makes. A new map is published
    /// whole on every change, so a reader on any thread sees one complete map.</summary>
    internal sealed class SourceMap
    {
        public static readonly SourceMap Empty = new(new Dictionary<string, string>(StringComparer.Ordinal));

        public readonly Dictionary<string, string> Text;
        public readonly HashSet<string> Keys = new(StringComparer.Ordinal);

        public SourceMap(Dictionary<string, string> text)
        {
            Text = text;
            foreach (var key in text.Keys)
                Keys.Add(key[(key.LastIndexOf('/') + 1)..]);
        }
    }

    internal static volatile SourceMap Source = SourceMap.Empty;

    private static MelonLogger.Instance _log;
    private static int _mainThread = -1;
    // The game's language the last apply was for. A store rebuilt for another language waits for
    // that language's apply.
    private static Il2CppMenace.Tools.LocaLanguage? _language;
    private static string _token;
    private static bool _sourceLanguage;
    private static readonly HashSet<string> Dirty = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Written> Applied = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);

    // The source language's replacements are the ones written into the store, so a key the game
    // lacks or a replacement that would not format is left out here as well.
    private static void PublishSource()
    {
        if (_token == null || !_sourceLanguage)
        {
            Source = SourceMap.Empty;
            return;
        }
        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, written) in Applied)
            text[key] = written.Text;
        Source = new SourceMap(text);
    }

    /// <summary>Write the replacements for the active language, identified by
    /// <paramref name="token"/>. Called on the main thread once the language is resolved and the
    /// mods' locale table is installed.</summary>
    public static void Apply(MelonLogger.Instance log, string token, bool sourceLanguage)
    {
        if (_log == null)
        {
            _log = log;
            _mainThread = Environment.CurrentManagedThreadId;
            Jiangyu.Sdk.Locale.ReplacementsChanged = OnReplaced;
        }

        var data = ReadData();
        if (data == null)
            return;

        // A new language's store holds the game's text in every entry, so each is recorded afresh.
        // A reload of the same language keeps the records, which hold the game's own text each
        // entry had before this first wrote it.
        _language = CurrentLanguage();
        var languageChanged = token != _token;
        if (languageChanged)
            Applied.Clear();
        var full = languageChanged || StoreRebuilt(data);
        _token = token;
        _sourceLanguage = sourceLanguage;

        var keys = TakeDirty();
        if (full)
            Write(data, keys: null);
        else if (keys.Count > 0)
            Write(data, keys);

        PublishSource();
    }

    private static void OnReplaced(string key)
    {
        lock (Dirty)
            Dirty.Add(key);
        if (Environment.CurrentManagedThreadId == _mainThread)
            WritePending();
    }

    /// <summary>Write the replacements made since the last write, once a language has been applied.
    /// Called on the main thread, on every scene load, for a replacement made on another
    /// thread.</summary>
    public static void WritePending()
    {
        if (_token == null)
            return;
        lock (Dirty)
            if (Dirty.Count == 0)
                return;
        var data = ReadData();
        // A store the game has rebuilt is left to the apply that follows. After a language change,
        // written now it would take the last language's text, which that apply would then record
        // as the game's own.
        if (data == null || CurrentLanguage() != _language || StoreRebuilt(data))
            return;
        Write(data, TakeDirty());
        PublishSource();
    }

    private static List<string> TakeDirty()
    {
        lock (Dirty)
        {
            var keys = new List<string>(Dirty);
            Dirty.Clear();
            return keys;
        }
    }

    private static Il2CppMenace.Tools.LocaLanguage? CurrentLanguage()
    {
        try { return LocaManager.Get()?.m_CurrentLanguage; }
        catch { return null; }
    }

    private static Il2CppMenace.Tools.LocaData ReadData()
    {
        try { return LocaManager.Get()?.GetData(); }
        catch (Exception ex)
        {
            _log?.Warning($"Game text: the loca store is not readable: {ex.Message}");
            return null;
        }
    }

    // An entry this wrote shows the game's own text again: the game reloaded the store. A reload in
    // the source language sets each entry's default from the CSV and leaves its translation, so the
    // default is checked too where this wrote it. Every entry is checked, since one may hold text
    // the game's own matches. An entry holding text from another writer, or gone, is not a reload.
    // With nothing written there is nothing of ours to lose.
    private static bool StoreRebuilt(Il2CppMenace.Tools.LocaData data)
    {
        foreach (var (key, written) in Applied)
        {
            try
            {
                // The game never removes an entry, so one gone is another writer's doing.
                var entry = FindEntry(data, key);
                if (entry == null)
                    continue;
                if (entry.Translation != written.Text && entry.Translation == written.OriginalTranslation)
                    return true;
                if (written.WroteDefault && entry.DefaultTranslation != written.Text
                    && entry.DefaultTranslation == written.OriginalDefault)
                    return true;
            }
            catch
            {
                // An entry that cannot be read says nothing of the others.
            }
        }
        return false;
    }

    private static Il2CppMenace.Tools.LocaEntry FindEntry(Il2CppMenace.Tools.LocaData data, string key)
    {
        var slash = key.IndexOf('/');
        if (slash <= 0)
            return null;
        var prefix = key[..slash];
        // Names only: Enum.TryParse would also take a number or a comma list.
        if (!Enum.TryParse<LocaCategory>(prefix, out var named) || !Enum.IsDefined(named) || named.ToString() != prefix)
            return null;
        var category = data.GetCategory(named);
        return category != null && category.HasEntry(key) ? category.GetEntry(key) : null;
    }

    private static void Write(Il2CppMenace.Tools.LocaData data, IEnumerable<string> keys)
    {
        var written = 0;
        var restored = 0;
        foreach (var (key, text) in Jiangyu.Sdk.Locale.Replacements(_sourceLanguage, keys))
        {
            try
            {
                var entry = FindEntry(data, key);
                if (entry == null)
                {
                    if (Reported.Add(key))
                        _log?.Warning($"Game text: the game has no string '{key}' to replace.");
                    Applied.Remove(key);
                    continue;
                }

                Applied.TryGetValue(key, out var prior);
                var ours = prior != null && entry.Translation == prior.Text;
                // The game's own English, whose placeholders every language's text takes.
                var original = prior != null ? prior.OriginalDefault : entry.DefaultTranslation;
                var fits = text == null || GameTextFormat.Fits(text, original);
                if (!fits && Reported.Add(key + "\n" + text))
                    _log?.Warning($"Game text: the replacement for '{key}' does not fit the game's placeholders in '{original}', so the game's text stands.");

                if (text == null || !fits)
                {
                    // Not translated for this language, or not usable: the game's own text stands.
                    if (ours)
                    {
                        entry.Translation = prior.OriginalTranslation;
                        if (prior.WroteDefault)
                            entry.DefaultTranslation = prior.OriginalDefault;
                        restored++;
                    }
                    Applied.Remove(key);
                    continue;
                }

                // An entry another writer changed since keeps the game's own text this took first,
                // so a restore and a reload still find the game's text.
                var record = ours
                    ? prior
                    : prior != null
                        ? new Written { OriginalTranslation = prior.OriginalTranslation, OriginalDefault = prior.OriginalDefault }
                        : new Written { OriginalTranslation = entry.Translation, OriginalDefault = entry.DefaultTranslation };
                record.Text = text;
                record.WroteDefault = _sourceLanguage;
                if (_sourceLanguage)
                    entry.DefaultTranslation = text;
                entry.Translation = text;
                Applied[key] = record;
                written++;
            }
            catch (Exception ex)
            {
                _log?.Warning($"Game text: '{key}' could not be replaced: {ex.Message}");
            }
        }
        if (_log != null && (written > 0 || restored > 0))
            LoaderDebug.Write(_log, $"Game text: {written} game string(s) replaced, {restored} restored.");
    }
}
