using System;
using System.Collections;
using MelonLoader;

namespace Jiangyu.Loader.Runtime;

// Decides WHEN replacements are applied (the ReplacementCoordinator decides what and
// how). On scene load it drops the old scene's caches and applies once, and a dense
// follow-up poll catches lazy-loaded assets. Late unit spawns are handled event-driven
// by the Element.OnSpawned postfix, not by this scheduler.
internal sealed class ReplacementScheduler
{
    // Post-scene-load poll schedule (frame offsets from scene load). The dense
    // every-5-frames window up to t=100 covers the initial load burst with at most ~83ms
    // of visible popin on lazy-loaded textures. The tail (150..600) catches late
    // stragglers from the scene's own asset streaming. Unit spawns that arrive later
    // (player deployment, reinforcement waves) are handled by the Element.OnSpawned
    // postfix, not this poll.
    private static readonly int[] PostSceneLoadPollFrames =
    {
        5, 10, 15, 20, 25, 30, 35, 40, 45, 50,
        55, 60, 65, 70, 75, 80, 85, 90, 95, 100,
        150, 250, 400, 600,
    };

    // Once the schedule has run out, a pass every this many frames for as long as template
    // work is deferred: a held block or clone (another loader registers templates on its own
    // clock), a type not live yet, or a scoped locale re-run. It lands the pass after the
    // wait ends, not the next scene.
    private const int HeldWorkPollFrames = 300;

    private readonly ReplacementCoordinator _coordinator;
    // Bumped on every scene change. The steady-state loop of a scene stops when the next
    // scene starts its own.
    private int _sceneGeneration;

    public ReplacementScheduler(ReplacementCoordinator coordinator) => _coordinator = coordinator;

    // Drop the previous scene's caches and re-arm the spawn monitor for the new scene.
    public void Reset()
    {
        _sceneGeneration++;
        _coordinator.OnSceneUnloaded();
    }

    public IEnumerator FollowUpPoll(MelonLogger.Instance log)
    {
        // The scene this schedule belongs to. The next scene starts its own, and this one
        // stops at the change rather than polling and reporting on the new scene's behalf.
        var generation = _sceneGeneration;
        var frame = 0;
        var scheduleIndex = 0;
        while (scheduleIndex < PostSceneLoadPollFrames.Length)
        {
            yield return null;
            if (generation != _sceneGeneration)
                yield break;
            frame++;
            if (frame >= PostSceneLoadPollFrames[scheduleIndex])
            {
                Apply(log);
                scheduleIndex++;
            }
        }

        _coordinator.OnPollScheduleComplete(log);

        while (generation == _sceneGeneration && _coordinator.HasDeferredTemplateWork)
        {
            for (var wait = 0; wait < HeldWorkPollFrames && generation == _sceneGeneration; wait++)
                yield return null;
            if (generation != _sceneGeneration)
                yield break;
            // Template work only: the renderer sweep, prefab rebind and texture pass belong
            // to the schedule's polls, not to a loop that may run for the whole scene.
            try
            {
                _coordinator.ApplyDeferredTemplateWork(log);
            }
            catch (Exception ex)
            {
                log.Error($"Template pass failed: {ex}");
            }

            // A block first held by this pass (its type went live after the schedule) is
            // reported here, once, like one the schedule end reported.
            _coordinator.ReportLate(log);
        }
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
