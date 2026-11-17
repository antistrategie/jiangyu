using Jiangyu.Loader.Logging;
using MelonLoader;
using LocaCategory = Il2CppMenace.Tools.LocaCategory;
using LocaManager = Il2CppMenace.Tools.LocaManager;

namespace Jiangyu.Loader.Runtime.Localisation;

/// <summary>
/// Writes the game strings mods replace (<see cref="Jiangyu.Sdk.Locale.Replace"/>) into the game's
/// loca store, in the entry under the game's own key. The key's first segment is its category, as
/// the game files it. A key the game has no entry for is reported once and left alone: a mod
/// replaces the game's text, it does not add keys.
///
/// <para>The game rebuilds the store from its CSV on every language change, so this runs after
/// each locale apply and switch. A full write happens for a new language or a rebuilt store, and
/// otherwise only the keys replaced since the last write are written. A replacement made on the main
/// thread once the store is loaded is written at once. One made on another thread waits for the
/// next apply. In the source language both the entry's default and its translation are written, as
/// the template injector does. In another language only the translation is, and only where the mod
/// translated it. A key the active translation does not cover gets the game's own text back, so an
/// earlier replacement for it does not stay.</para>
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

    private static MelonLogger.Instance _log;
    private static int _mainThread = -1;
    private static string _token;
    private static bool _sourceLanguage;
    private static readonly HashSet<string> Dirty = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Written> Applied = new(StringComparer.Ordinal);
    private static readonly HashSet<string> MissingReported = new(StringComparer.Ordinal);

    /// <summary>The replacements by full key while the source language is active, which the game's
    /// code reads past the store (<see cref="GameTextTranslatePatch"/>), and the last segment of
    /// each key, the one check every Translate call makes. Empty in any other language.</summary>
    internal static readonly Dictionary<string, string> SourceText = new(StringComparer.Ordinal);
    internal static readonly HashSet<string> SourceKeys = new(StringComparer.Ordinal);

    // Rebuilds both sets for a new language. Within one language only the keys a mod replaced
    // since are updated (UpdateSourceText), as a replacement is never withdrawn.
    private static void RefreshSourceText()
    {
        SourceText.Clear();
        SourceKeys.Clear();
        UpdateSourceText(keys: null);
    }

    private static void UpdateSourceText(IEnumerable<string> keys)
    {
        if (_token == null || !_sourceLanguage)
            return;
        foreach (var (key, text) in Jiangyu.Sdk.Locale.Replacements(sourceLanguage: true, keys))
        {
            var slash = key.LastIndexOf('/');
            if (text == null || slash < 0)
                continue;
            SourceText[key] = text;
            SourceKeys.Add(key[(slash + 1)..]);
        }
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

        var languageChanged = token != _token;
        var full = languageChanged || StoreRebuilt(data);
        if (full)
            Applied.Clear();
        _token = token;
        _sourceLanguage = sourceLanguage;

        var keys = TakeDirty();
        if (full)
            Write(data, keys: null);
        else if (keys.Count > 0)
            Write(data, keys);

        if (languageChanged)
            RefreshSourceText();
        else if (keys.Count > 0)
            UpdateSourceText(keys);
    }

    private static void OnReplaced(string key)
    {
        lock (Dirty)
            Dirty.Add(key);
        if (_token == null || Environment.CurrentManagedThreadId != _mainThread)
            return;
        var data = ReadData();
        if (data == null)
            return;
        var keys = TakeDirty();
        Write(data, keys);
        UpdateSourceText(keys);
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

    private static Il2CppMenace.Tools.LocaData ReadData()
    {
        try { return LocaManager.Get()?.GetData(); }
        catch (Exception ex)
        {
            _log?.Warning($"Game text: the loca store is not readable: {ex.Message}");
            return null;
        }
    }

    // One entry this wrote no longer holds its text: the game reloaded the store. With nothing
    // written there is nothing of ours to lose.
    private static bool StoreRebuilt(Il2CppMenace.Tools.LocaData data)
    {
        foreach (var (key, written) in Applied)
        {
            try
            {
                var entry = FindEntry(data, key);
                return entry == null || entry.Translation != written.Text;
            }
            catch
            {
                return true;
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
                    if (MissingReported.Add(key))
                        _log?.Warning($"Game text: the game has no string '{key}' to replace.");
                    continue;
                }

                Applied.TryGetValue(key, out var prior);
                var ours = prior != null && entry.Translation == prior.Text;

                if (text == null)
                {
                    // Not translated for this language: the game's own text stands.
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

                var record = ours
                    ? prior
                    : new Written { OriginalTranslation = entry.Translation, OriginalDefault = entry.DefaultTranslation };
                record.Text = text;
                record.WroteDefault |= _sourceLanguage;
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
