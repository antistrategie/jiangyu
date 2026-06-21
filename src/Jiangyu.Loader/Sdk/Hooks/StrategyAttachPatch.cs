using Il2CppMenace.States;
using Il2CppMenace.Strategy;
using Jiangyu.Loader.Runtime.Patching;
using MelonLoader;

namespace Jiangyu.Loader.Sdk.Hooks;

/// <summary>
/// Attaches the strategy hook publisher when a <see cref="StrategyState"/> becomes active
/// and as its factions load, by Harmony-postfixing <c>StrategyState.OnAdded</c> and the
/// <c>StoryFactions</c> population methods (<c>Init</c> for a new campaign,
/// <c>ProcessSaveState</c> for a loaded save). The publisher is created after this patch
/// installs, so it is handed in via the static <see cref="Publisher"/>.
/// </summary>
internal sealed class StrategyAttachPatch : IHarmonyPatchModule
{
    internal static StrategyHookPublisher Publisher;
    private static MelonLogger.Instance _log;

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        _log = context.Log;
        HarmonyPatching.TryPostfix(harmony, "Il2CppMenace.States.StrategyState", "OnAdded",
            typeof(StrategyAttachPatch), nameof(OnAddedPostfix), _log, "strategy hooks");
        HarmonyPatching.TryPostfix(harmony, "Il2CppMenace.Strategy.StoryFactions", "Init",
            typeof(StrategyAttachPatch), nameof(FactionsChangedPostfix), _log, "strategy hooks");
        HarmonyPatching.TryPostfix(harmony, "Il2CppMenace.Strategy.StoryFactions", "ProcessSaveState",
            typeof(StrategyAttachPatch), nameof(FactionsChangedPostfix), _log, "strategy hooks");
    }

    private static void OnAddedPostfix(StrategyState __instance) => Publisher?.AttachState(__instance);
    private static void FactionsChangedPostfix(StoryFactions __instance) => Publisher?.SubscribeFactionsFrom(__instance);
}
