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
    // every-5-frames window up to t=100 covers the initial load burst with at
    // most ~83ms of visible popin on lazy-loaded textures. Exponential tail
    // (150..600) catches late stragglers. After t=600 the steady-state spawn
    // monitor below takes over so unit spawns arriving late (player-initiated
    // deployment, reinforcement waves) also get their mesh/prefab swaps.
    private static readonly int[] PostSceneLoadPollFrames =
    {
        5, 10, 15, 20, 25, 30, 35, 40, 45, 50,
        55, 60, 65, 70, 75, 80, 85, 90, 95, 100,
        150, 250, 400, 600,
    };

    // Steady-state spawn monitor: after the scheduled scene-load polls finish
    // we keep sweeping at a low cadence so newly-spawned SMRs/prefabs (e.g.
    // player-deployed soldiers) still receive their replacement. Mesh and
    // prefab sweeps are cheap (O(active SMRs)). Texture mutation is NOT run in
    // this monitor — textures don't appear on user-spawn events, only at scene
    // or asset-stream time which the scheduled window already covers.
    // 10 frames ≈ 170ms at 60fps — fast enough that the default mesh is barely
    // visible before the swap lands.
    private const int SpawnMonitorIntervalFrames = 10;

    private static readonly int[] DelayedInspectFrames = { 60, 180, 600 };
    private const int PeriodicInspectIntervalFrames = 300; // ~5s; dumps continue indefinitely while flag set

    private readonly ReplacementCoordinator _replacementCoordinator = new();
    private int _frameInScene;
    private int _frameOfLastPeriodicDump;
    private int _frameOfLastSpawnMonitor;
    private string _currentScene;
    private readonly HashSet<int> _inspectedFrames = new();

    public override void OnInitializeMelon()
    {
        LoggerInstance.Msg("Jiangyu loader initialising...");

        var modsDir = Path.Combine(MelonEnvironment.MelonBaseDirectory, "Mods");
        var loadSummary = _replacementCoordinator.LoadBundles(modsDir, LoggerInstance);

        LoggerInstance.Msg(
            $"Resolved {loadSummary.LoadableModCount} loadable mod(s), skipped {loadSummary.BlockedModCount} blocked mod(s), loaded {loadSummary.LoadedBundleCount} bundle(s).");

        _replacementCoordinator.InstallHarmonyPatches(HarmonyInstance, LoggerInstance);
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        _currentScene = sceneName;
        _frameInScene = 0;
        _frameOfLastPeriodicDump = 0;
        _frameOfLastSpawnMonitor = 0;
        _inspectedFrames.Clear();
        _replacementCoordinator.OnSceneUnloaded();

        LoggerInstance.Msg($"Scene loaded: {sceneName} ({buildIndex})");

        if (RuntimeInspector.IsEnabled())
        {
            RuntimeInspector.Dump(sceneName + "-t0", buildIndex, LoggerInstance);
        }

        TryApply();
        MelonCoroutines.Start(FollowUpPoll());
    }

    public override void OnUpdate()
    {
        _frameInScene++;

        if (RuntimeInspector.IsEnabled() && !string.IsNullOrEmpty(_currentScene))
        {
            foreach (var markFrame in DelayedInspectFrames)
            {
                if (_frameInScene == markFrame && _inspectedFrames.Add(markFrame))
                {
                    RuntimeInspector.Dump($"{_currentScene}-t{markFrame}f", 0, LoggerInstance);
                }
            }

            if (_frameInScene >= 600 &&
                _frameInScene - _frameOfLastPeriodicDump >= PeriodicInspectIntervalFrames)
            {
                _frameOfLastPeriodicDump = _frameInScene;
                RuntimeInspector.Dump($"{_currentScene}-t{_frameInScene}f", 0, LoggerInstance);
            }
        }

        // Steady-state spawn monitor. After the scheduled scene-load polls
        // finish (t=600), sweep for newly-spawned SMRs every
        // SpawnMonitorIntervalFrames so player-deployed units / reinforcement
        // waves get their mesh/prefab swaps. Only runs when at least one mesh
        // or prefab replacement is registered — texture-only mods have no
        // target surface here and the monitor would be pure overhead.
        if (_frameInScene >= 600 &&
            _replacementCoordinator.HasMeshOrPrefabReplacements &&
            _frameInScene - _frameOfLastSpawnMonitor >= SpawnMonitorIntervalFrames)
        {
            _frameOfLastSpawnMonitor = _frameInScene;
            TryApply(includeTextures: false);
        }

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

    private void TryApply(bool includeTextures = true)
    {
        try
        {
            _replacementCoordinator.ApplyReplacements(LoggerInstance, includeTextures);
        }
        catch (Exception ex)
        {
            LoggerInstance.Error($"Apply failed: {ex}");
        }
    }
}
