using System;
using System.Collections;
using MelonLoader;

namespace Jiangyu.Loader.Runtime;

// Decides WHEN replacements are applied (the ReplacementCoordinator decides what and
// how). On scene load it drops the old scene's caches and applies once, a dense
// follow-up poll catches lazy-loaded assets, and a steady-state spawn monitor keeps
// sweeping for late spawns.
internal sealed class ReplacementScheduler
{
    // Post-scene-load poll schedule (frame offsets from scene load). The dense
    // every-5-frames window up to t=100 covers the initial load burst with at most ~83ms
    // of visible popin on lazy-loaded textures. The tail (150..600) catches late
    // stragglers. After t=600 the steady-state spawn monitor takes over so unit spawns
    // arriving late (player deployment, reinforcement waves) also get their swaps.
    private static readonly int[] PostSceneLoadPollFrames =
    {
        5, 10, 15, 20, 25, 30, 35, 40, 45, 50,
        55, 60, 65, 70, 75, 80, 85, 90, 95, 100,
        150, 250, 400, 600,
    };

    // Steady-state spawn monitor cadence. Mesh and prefab sweeps are cheap (O(active
    // SMRs)); texture mutation is NOT run here, as textures appear only at scene or
    // asset-stream time which the scheduled window already covers. 10 frames is about
    // 170ms at 60fps, fast enough that the default mesh is barely visible before the swap.
    private const int SpawnMonitorIntervalFrames = 10;

    private readonly ReplacementCoordinator _coordinator;
    private int _frameOfLastSpawnMonitor;

    public ReplacementScheduler(ReplacementCoordinator coordinator) => _coordinator = coordinator;

    // Drop the previous scene's caches and re-arm the spawn monitor for the new scene.
    public void Reset()
    {
        _frameOfLastSpawnMonitor = 0;
        _coordinator.OnSceneUnloaded();
    }

    public IEnumerator FollowUpPoll(MelonLogger.Instance log)
    {
        var frame = 0;
        var scheduleIndex = 0;
        while (scheduleIndex < PostSceneLoadPollFrames.Length)
        {
            yield return null;
            frame++;
            if (frame >= PostSceneLoadPollFrames[scheduleIndex])
            {
                Apply(log);
                scheduleIndex++;
            }
        }
    }

    // The per-frame steady-state work: sweep for late spawns once the scheduled polls
    // finish (t=600), and let the coordinator advance any driven replacements. The spawn
    // sweep only runs when a mesh or prefab replacement is registered, since texture-only
    // mods have no target surface here.
    public void Tick(int frameInScene, MelonLogger.Instance log)
    {
        if (frameInScene >= 600
            && _coordinator.HasMeshOrPrefabReplacements
            && frameInScene - _frameOfLastSpawnMonitor >= SpawnMonitorIntervalFrames)
        {
            _frameOfLastSpawnMonitor = frameInScene;
            Apply(log, includeTextures: false);
        }

        _coordinator.UpdateDrivenReplacements(log);
    }

    // Apply registered replacements now. Textures only land at scene or asset-stream
    // time, so the steady-state spawn sweep passes includeTextures: false.
    public void Apply(MelonLogger.Instance log, bool includeTextures = true)
    {
        try
        {
            _coordinator.ApplyReplacements(log, includeTextures);
        }
        catch (Exception ex)
        {
            log.Error($"Apply failed: {ex}");
        }
    }
}
