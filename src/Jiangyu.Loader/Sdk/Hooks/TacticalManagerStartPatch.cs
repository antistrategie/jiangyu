using Il2CppMenace.Tactical;
using Jiangyu.Loader.Runtime.Patching;
using MelonLoader;

namespace Jiangyu.Loader.Sdk.Hooks;

/// <summary>
/// Attaches the tactical hook publisher when a mission's <see cref="TacticalManager"/>
/// comes up, by Harmony-postfixing its <c>Start</c>. The publisher is created after this
/// patch installs, so it is handed in via the static <see cref="Publisher"/> and the
/// postfix attaches through it once it is set.
/// </summary>
internal sealed class TacticalManagerStartPatch : IHarmonyPatchModule
{
    internal static TacticalHookPublisher Publisher;
    private static MelonLogger.Instance _log;

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        _log = context.Log;
        HarmonyPatching.TryPostfix(
            harmony, "Il2CppMenace.Tactical.TacticalManager", "Start",
            typeof(TacticalManagerStartPatch), nameof(StartPostfix), _log, "tactical hooks");
    }

    private static void StartPostfix(TacticalManager __instance) => Publisher?.Attach(__instance);
}
