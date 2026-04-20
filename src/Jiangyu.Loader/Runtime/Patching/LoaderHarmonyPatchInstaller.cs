namespace Jiangyu.Loader.Runtime.Patching;

/// <summary>
/// Installs the loader's explicit Harmony patch modules from one place. This
/// keeps hook registration extendable without hiding which concerns are
/// patched or why.
/// </summary>
internal sealed class LoaderHarmonyPatchInstaller
{
    private readonly IReadOnlyList<IHarmonyPatchModule> _modules;

    public LoaderHarmonyPatchInstaller(IEnumerable<IHarmonyPatchModule> modules)
    {
        _modules = modules.ToArray();
    }

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        foreach (var module in _modules)
            module.Install(harmony, context);
    }
}
