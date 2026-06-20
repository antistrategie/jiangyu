using System.Reflection;
using Jiangyu.Loader.Runtime.Patching;
using Jiangyu.Shared.Localisation;
using MelonLoader;

namespace Jiangyu.Loader.Runtime.Localisation;

/// <summary>
/// Reads the active language from MENACE's <c>LocaManager</c> and maps it to a locale code. Bound
/// reflectively, like the loader's other game-method hooks, so the loader keeps no compile-time
/// dependency on the game assembly.
/// </summary>
internal static class LocaleResolver
{
    internal enum State
    {
        /// <summary>LocaManager is not up yet (or could not be read). Try again on a later pass.</summary>
        NotReady,

        /// <summary>The current language is the source (English). No translation table applies.</summary>
        Source,

        /// <summary>A translatable locale is active. Its code is returned.</summary>
        Translatable,
    }

    private static bool _bound;
    private static MethodInfo _get;
    private static MethodInfo _getCurrentLanguage;

    public static (State State, string Code, string Language) Resolve(MelonLogger.Instance log)
    {
        if (!_bound)
        {
            _get = Il2CppMethodResolver.Find("LocaManager", "Get", Array.Empty<string>(), exact: true, log, "Locale apply");
            _getCurrentLanguage = Il2CppMethodResolver.Find(
                "LocaManager", "GetCurrentLanguage", Array.Empty<string>(), exact: true, log, "Locale apply");
            // Latch only once both resolve, so a call before the game assembly is mapped retries
            // rather than wedging localisation off for the session.
            _bound = _get != null && _getCurrentLanguage != null;
        }

        if (_get == null || _getCurrentLanguage == null)
            return (State.NotReady, null, null);

        object instance;
        try { instance = _get.Invoke(null, null); }
        catch { return (State.NotReady, null, null); }
        if (instance == null)
            return (State.NotReady, null, null);

        object language;
        try { language = _getCurrentLanguage.Invoke(instance, null); }
        catch { return (State.NotReady, null, null); }

        var name = language?.ToString();
        if (string.IsNullOrEmpty(name))
            return (State.NotReady, null, null);
        if (name == LocaleCatalogue.SourceLanguageName)
            return (State.Source, null, name);

        var code = LocaleCatalogue.CodeForLanguageName(name);
        return code == null ? (State.Source, null, name) : (State.Translatable, code, name);
    }
}
