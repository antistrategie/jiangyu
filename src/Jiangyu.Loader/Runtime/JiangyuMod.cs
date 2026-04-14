// TODO: Two Unity 6 + MelonLoader 0.7.2 bugs affect this loader. Both are fixed in
// MelonLoader alpha-development — remove workarounds once stable ships.
//
// 1. ICall signature changes: _Injected suffixes, ManagedSpanWrapper params, GC handle
//    returns. Worked around in BundleLoader.cs. (LavaGang/MelonLoader#1122)
//
// 2. GetPinnableReference crash: game assembly methods that take string params internally
//    call ReadOnlySpan<char>.GetPinnableReference() which throws MissingMethodException
//    in Il2CppInterop. Avoid calling game assembly methods with string params directly.
//    (MelonLoader alpha commit 96c78935)

using MelonLoader;
using MelonLoader.Utils;

[assembly: MelonInfo(typeof(Jiangyu.Loader.Runtime.JiangyuMod), "Jiangyu", "0.1.0", "antistrategie")]
[assembly: MelonGame("Overhype Studios", "Menace")]

namespace Jiangyu.Loader.Runtime;

public class JiangyuMod : MelonMod
{
    private readonly ReplacementCoordinator _replacementCoordinator = new();
    private bool _autoApplied;

    public override void OnInitializeMelon()
    {
        LoggerInstance.Msg("Jiangyu loader initialising...");

        var modsDir = Path.Combine(MelonEnvironment.MelonBaseDirectory, "Mods");
        var bundleCount = _replacementCoordinator.LoadBundles(modsDir, LoggerInstance);

        LoggerInstance.Msg($"Loaded {bundleCount} bundle(s).");
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        _autoApplied = false;
        LoggerInstance.Msg($"Scene loaded: {sceneName} ({buildIndex})");
    }

    public override void OnUpdate()
    {
        if (!_autoApplied && _replacementCoordinator.HasReplacementTargets())
        {
            try
            {
                LoggerInstance.Msg("Detected matching runtime replacement targets — applying replacements...");
                _replacementCoordinator.ApplyReplacements(LoggerInstance);
                _autoApplied = true;
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Auto-apply failed: {ex}");
                _autoApplied = true;
            }
        }

        _replacementCoordinator.UpdateDrivenReplacements(LoggerInstance);
    }
}
