using HarmonyLib;
using Jiangyu.Loader.Runtime.Patching;
using Il2CppMenace.Tools;

namespace Jiangyu.Loader.Runtime.Localisation;

/// <summary>
/// Shows the game strings mods replace (<see cref="Jiangyu.Sdk.Locale.Replace"/>) where the game's
/// own code asks for them in the source language. <c>Loca.Translate</c> is how the game's code
/// fetches its interface text, by category, subcategory, key and the English it was written with.
/// In the source language it returns that English as given and never reads the loca store, so
/// the store write <see cref="GameTextReplacer"/> makes reaches every language but that one. A
/// postfix returns the replacement instead. Every other way the game's code asks for text (the
/// format, notification, dialog and enum helpers, and the overload without a key) goes through this
/// overload, so the one postfix covers them. In another language Translate reads the store, where
/// the replacement already is.
/// </summary>
internal sealed class GameTextTranslatePatch : IHarmonyPatchModule
{
    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        try
        {
            var method = AccessTools.Method(typeof(Loca), nameof(Loca.Translate), new[]
            {
                typeof(LocaCategory), typeof(string), typeof(string), typeof(string), typeof(bool), typeof(LocaEntryType), typeof(bool),
            });
            if (method == null)
            {
                context.Log.Warning("Game text: Loca.Translate was not found. Replaced interface strings show in other languages only.");
                return;
            }
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(GameTextTranslatePatch), nameof(Postfix)));
            HarmonyPatching.Installed(context.Log, "Game text: hooked Loca.Translate.");
        }
        catch (Exception ex)
        {
            context.Log.Warning($"Game text: patching Loca.Translate failed: {ex.Message}");
        }
    }

    // Translate is behind nearly every string the game's code shows, so a key that is not
    // replaced costs one set lookup.
    private static void Postfix(LocaCategory __0, string __1, string __2, ref string __result)
    {
        if (__2 == null || !GameTextReplacer.SourceKeys.Contains(__2))
            return;
        if (GameTextReplacer.SourceText.TryGetValue($"{__0}/{__1}/{__2}", out var text))
            __result = text;
    }
}
