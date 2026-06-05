using System;
using System.Collections.Generic;
using Jiangyu.Loader.Diagnostics;
using Jiangyu.Loader.Diagnostics.InjectionGate;
using Jiangyu.Loader.Diagnostics.UiProbe;
using Jiangyu.Loader.Diagnostics.VerbProbe;
using MelonLoader;

namespace Jiangyu.Loader.Runtime;

// Drives the dev-flag-gated diagnostics each scene and frame: the load-time and
// periodic scene/template dumps, the injection-gate spike, the verb-surface spike, and
// the UI-tree probe. Everything here is gated by a dev flag (InspectionSink, or the
// per-probe IsEnabled), so it is inert in a normal play session.
internal sealed class InspectionDriver
{
    private static readonly int[] DelayedInspectFrames = { 60, 180, 600 };
    private static readonly int[] TemplatesInspectFrames = { 180, 600 };
    private static readonly HashSet<string> TemplatesInspectScenes = new(StringComparer.Ordinal)
    {
        "Strategy",
        "MissionPreparation",
        "Tactical",
    };
    private const int PeriodicInspectIntervalFrames = 300; // ~5s; dumps continue indefinitely while the flag is set.

    private readonly HashSet<int> _inspectedFrames = new();
    private readonly HashSet<int> _templatesInspectAttempts = new();
    private bool _templatesDumpSucceeded;
    private int _frameOfLastPeriodicDump;
    private bool _gateLiveDone;
    private bool _verbsLiveDone;

    // Re-arm the per-scene latches for the new scene. The orchestrator refreshes the dev
    // flags; DumpSceneLoad runs the load-time snapshot separately, after the mods are
    // notified, to match the original dump ordering.
    public void OnSceneLoaded()
    {
        _inspectedFrames.Clear();
        _templatesInspectAttempts.Clear();
        _templatesDumpSucceeded = false;
        _frameOfLastPeriodicDump = 0;
        _gateLiveDone = false;
        _verbsLiveDone = false;

        UiTreeProbe.Reset();
    }

    // The load-time scene snapshot (dev-flag-gated). Runs after the mods' OnSceneLoaded so
    // it captures the same post-notify state the original inline dump did.
    public void DumpSceneLoad(int buildIndex, string scene, MelonLogger.Instance log)
    {
        if (InspectionSink.IsEnabled())
            SceneIdentityInspector.Dump(scene + "-t0", buildIndex, log);
    }

    public void Tick(int frameInScene, string scene, MelonLogger.Instance log)
    {
        RunLiveSpikes(frameInScene, scene, log);
        RunDumps(frameInScene, scene, log);
    }

    // One-shot injection-gate and verb-surface spikes (Tactical only, retried every ~300
    // frames until an active actor is present), and the UI-tree probe that dumps the live
    // screen tree on its own cadence so a modder can find injection attach points.
    private void RunLiveSpikes(int frameInScene, string scene, MelonLogger.Instance log)
    {
        if (!_gateLiveDone && scene == "Tactical"
            && frameInScene >= 300 && frameInScene % 300 == 0
            && InjectionGateInspector.IsEnabled())
        {
            _gateLiveDone = InjectionGateInspector.RunLiveIfReady($"Tactical-t{frameInScene}f", log);
        }

        if (!_verbsLiveDone && scene == "Tactical"
            && frameInScene >= 300 && frameInScene % 300 == 0
            && VerbSurfaceProbe.IsEnabled())
        {
            _verbsLiveDone = VerbSurfaceProbe.RunLiveIfReady($"Tactical-t{frameInScene}f", log);
        }

        if (frameInScene >= 120 && frameInScene % 120 == 0 && UiTreeProbe.IsEnabled())
            UiTreeProbe.Tick($"{scene}-t{frameInScene}f", log);
    }

    // Scene-identity dumps at the delayed marks and on a periodic cadence, plus the
    // template-state dump retried until it lands once per relevant scene.
    private void RunDumps(int frameInScene, string scene, MelonLogger.Instance log)
    {
        if (!InspectionSink.IsEnabled() || string.IsNullOrEmpty(scene))
            return;

        foreach (var markFrame in DelayedInspectFrames)
            if (frameInScene == markFrame && _inspectedFrames.Add(markFrame))
                SceneIdentityInspector.Dump($"{scene}-t{markFrame}f", 0, log);

        if (!_templatesDumpSucceeded && TemplatesInspectScenes.Contains(scene))
            foreach (var markFrame in TemplatesInspectFrames)
                if (frameInScene == markFrame && _templatesInspectAttempts.Add(markFrame))
                    _templatesDumpSucceeded =
                        TemplateStateInspector.TryDumpTemplatesFromLoader($"{scene}-t{markFrame}f", log);

        if (frameInScene >= 600 && frameInScene - _frameOfLastPeriodicDump >= PeriodicInspectIntervalFrames)
        {
            _frameOfLastPeriodicDump = frameInScene;
            SceneIdentityInspector.Dump($"{scene}-t{frameInScene}f", 0, log);
        }
    }
}
