using HarmonyLib;
using Jiangyu.Loader.Runtime.Patching;
using MelonLoader;

namespace Jiangyu.Loader.Runtime.Localisation;

/// <summary>
/// Re-applies mod translations when the player changes language in-game. The settings menu calls
/// <c>LocaManager.SetCurrentLanguage</c>. A postfix on it (and on <c>ReloadCurrentLanguage</c> as a
/// backstop) overlays the active mod translations so an in-game switch updates mod text without a
/// restart. The load-time pass already covers the case where the game starts in the chosen language.
/// </summary>
internal sealed class LocaleReloadPatch : IHarmonyPatchModule
{
    private const string Label = "Locale reload";
    private static MelonLogger.Instance _log;

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        _log = context.Log;

        var postfix = new HarmonyMethod(typeof(LocaleReloadPatch), nameof(Postfix));
        var hooked = false;
        hooked |= TryHook(harmony, "SetCurrentLanguage", new[] { "LocaLanguage" }, postfix);
        hooked |= TryHook(harmony, "ReloadCurrentLanguage", Array.Empty<string>(), postfix);

        if (!hooked)
            _log.Warning($"{Label}: no LocaManager language-change method found; an in-game language switch will not re-apply mod translations.");
    }

    private static bool TryHook(HarmonyLib.Harmony harmony, string methodName, string[] paramSuffixes, HarmonyMethod postfix)
    {
        var method = Il2CppMethodResolver.Find("LocaManager", methodName, paramSuffixes, exact: true, _log, Label);
        if (method == null)
            return false;
        try
        {
            harmony.Patch(method, postfix: postfix);
            _log.Msg($"{Label}: hooked LocaManager.{methodName}.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning($"{Label}: patch of {methodName} failed: {ex.Message}");
            return false;
        }
    }

    public static void Postfix()
    {
        try { LocaleApplier.NotifyLanguageReloaded(_log); }
        catch (Exception ex) { _log?.Warning($"{Label}: re-apply failed: {ex.Message}"); }
    }
}
