using System;
using Il2CppMenace.UI;
using Jiangyu.Game.Ui;
using Jiangyu.Loader.Logging;
using Jiangyu.Loader.Runtime.Patching;
using MelonLoader;

namespace Jiangyu.Loader.Runtime;

/// <summary>
/// Re-applies mod UI injections when the game brings a screen up, by Harmony-postfixing
/// both <c>UIManager.OpenScreen</c> (a fresh open, the path the strategy screens actually
/// take) and <c>UIManager.ActivateScreen</c> (a re-activation, e.g. back-navigation).
/// Replaces the per-frame active-screen poll: <see cref="UI"/> re-applies immediately and
/// hooks the screen root's GeometryChangedEvent so content built after the open still
/// lands, without a settle loop.
/// </summary>
internal sealed class UiInjectionActivatePatch : IHarmonyPatchModule
{
    private static MelonLogger.Instance _log;

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        _log = context.Log;
        HarmonyPatching.TryPostfix(harmony, "Il2CppMenace.UI.UIManager", "ActivateScreen",
            typeof(UiInjectionActivatePatch), nameof(ActivateScreenPostfix), _log, "ui injection");
        HarmonyPatching.TryPostfix(harmony, "Il2CppMenace.UI.UIManager", "OpenScreen",
            typeof(UiInjectionActivatePatch), nameof(OpenScreenPostfix), _log, "ui injection");
    }

    private static void ActivateScreenPostfix(UIScreen __0) => OnScreen(__0, "ActivateScreen");
    private static void OpenScreenPostfix(UIScreen __result) => OnScreen(__result, "OpenScreen");

    private static void OnScreen(UIScreen screen, string via)
    {
        try
        {
            if (LoaderDebug.Enabled)
            {
                string id;
                try { id = screen == null ? "<null>" : screen.name; }
                catch { id = "<?>"; }
                _log?.Debug($"[ui] {via} '{id}'");
            }

            UI.NotifyScreenActivated(screen == null ? null : screen.GetRootElement());
        }
        catch (Exception ex)
        {
            _log?.Error($"ui injection: {via} postfix threw: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
