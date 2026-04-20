namespace Jiangyu.Loader.Runtime.Patching;

/// <summary>
/// Explicit loader-owned Harmony patch module. Each module owns one concern
/// and installs the exact MENACE hooks it needs.
/// </summary>
internal interface IHarmonyPatchModule
{
    void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context);
}
