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

[assembly: MelonInfo(typeof(Jiangyu.Loader.JiangyuMod), "Jiangyu", "0.1.0", "antistrategie")]
[assembly: MelonGame("Overhype Studios", "Menace")]

namespace Jiangyu.Loader;

public class JiangyuMod : MelonMod
{
    private readonly MeshReplacer _meshReplacer = new();
    private bool _autoApplied;

    public override void OnInitializeMelon()
    {
        LoggerInstance.Msg("Jiangyu loader initialising...");

        var modsDir = Path.Combine(MelonEnvironment.MelonBaseDirectory, "Mods");
        var bundleCount = _meshReplacer.LoadBundles(modsDir, LoggerInstance);

        LoggerInstance.Msg($"Loaded {bundleCount} bundle(s).");
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        _autoApplied = false;
        LoggerInstance.Msg($"Scene loaded: {sceneName} ({buildIndex})");
    }

    public override void OnUpdate()
    {
        if (!_autoApplied && _meshReplacer.HasReplacementTargets())
        {
            try
            {
                LoggerInstance.Msg("Detected matching skinned meshes — auto-applying replacements...");
                _meshReplacer.ApplyReplacements(LoggerInstance);
                _autoApplied = true;
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Auto-apply failed: {ex}");
                _autoApplied = true;
            }
        }

        _meshReplacer.UpdateDrivenReplacements(LoggerInstance);

        // F9 remains as a manual retry trigger during testing.
        if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F9))
        {
            try
            {
                LoggerInstance.Msg("F9 pressed — applying replacements...");
                _meshReplacer.ApplyReplacements(LoggerInstance);
                _autoApplied = true;
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Manual apply failed: {ex}");
            }
        }
    }
}
