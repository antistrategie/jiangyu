using MelonLoader;

namespace Jiangyu.Loader.Runtime.Patching;

/// <summary>
/// Shared services available while installing Harmony patch modules. Extend
/// this with additional loader-owned dependencies as new patch families land.
/// </summary>
internal sealed class LoaderHarmonyPatchContext
{
    public LoaderHarmonyPatchContext(MelonLogger.Instance log)
    {
        Log = log;
    }

    public MelonLogger.Instance Log { get; }
}
