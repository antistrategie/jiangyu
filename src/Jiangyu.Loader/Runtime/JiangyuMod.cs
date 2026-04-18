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

using System.Collections;
using Jiangyu.Loader.Diagnostics;
using MelonLoader;
using MelonLoader.Utils;

[assembly: MelonInfo(typeof(Jiangyu.Loader.Runtime.JiangyuMod), "Jiangyu", "0.1.0", "antistrategie")]
[assembly: MelonGame("Overhype Studios", "Menace")]

namespace Jiangyu.Loader.Runtime;

public class JiangyuMod : MelonMod
{
    // Post-scene-load poll schedule (frame offsets from OnSceneWasLoaded). Dense
    // every-5-frames window up to t=100 keeps visible popin under ~83ms across
    // the active load window MENACE uses for its scene-start asset streaming.
    // Exponential tail past t=100 catches late stragglers up to ~10s. Sweeps
    // are idempotent via TextureMutationService, so repeat polls only do work
    // when something new has appeared.
    private static readonly int[] PostSceneLoadPollFrames =
    {
        5, 10, 15, 20, 25, 30, 35, 40, 45, 50,
        55, 60, 65, 70, 75, 80, 85, 90, 95, 100,
        150, 250, 400, 600,
    };

    private readonly ReplacementCoordinator _replacementCoordinator = new();

    public override void OnInitializeMelon()
    {
        LoggerInstance.Msg("Jiangyu loader initialising...");

        var modsDir = Path.Combine(MelonEnvironment.MelonBaseDirectory, "Mods");
        var loadSummary = _replacementCoordinator.LoadBundles(modsDir, LoggerInstance);

        LoggerInstance.Msg(
            $"Resolved {loadSummary.LoadableModCount} loadable mod(s), skipped {loadSummary.BlockedModCount} blocked mod(s), loaded {loadSummary.LoadedBundleCount} bundle(s).");
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        LoggerInstance.Msg($"Scene loaded: {sceneName} ({buildIndex})");

        if (RuntimeInspector.IsEnabled())
        {
            RuntimeInspector.Dump(sceneName, buildIndex, LoggerInstance);
        }

        TryApply();
        MelonCoroutines.Start(FollowUpPoll());
    }

    public override void OnUpdate()
    {
        _replacementCoordinator.UpdateDrivenReplacements(LoggerInstance);
    }

    private IEnumerator FollowUpPoll()
    {
        var frame = 0;
        var scheduleIndex = 0;
        while (scheduleIndex < PostSceneLoadPollFrames.Length)
        {
            yield return null;
            frame++;
            if (frame >= PostSceneLoadPollFrames[scheduleIndex])
            {
                TryApply();
                scheduleIndex++;
            }
        }
    }

    private void TryApply()
    {
        try
        {
            _replacementCoordinator.ApplyReplacements(LoggerInstance);
        }
        catch (Exception ex)
        {
            LoggerInstance.Error($"Apply failed: {ex}");
        }
    }
}
