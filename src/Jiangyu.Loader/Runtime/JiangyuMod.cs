using System.Collections;
using System.Reflection;
using Jiangyu.Loader.Diagnostics;
using Jiangyu.Loader.Diagnostics.InjectionGate;
using Jiangyu.Loader.Diagnostics.VerbProbe;
using Jiangyu.Loader.Logging;
using Jiangyu.Loader.Sdk;
using Jiangyu.Loader.Sdk.Hooks;
using Jiangyu.Loader.Sdk.Patches;
using Jiangyu.Loader.Sdk.State;
using Jiangyu.Loader.Sdk.Types;
using MelonLoader;
using MelonLoader.Utils;

[assembly: MelonInfo(typeof(Jiangyu.Loader.Runtime.JiangyuMod), "Jiangyu", Jiangyu.Loader.BuildInfo.Version, "antistrategie")]
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
    private static readonly int[] TemplatesInspectFrames = { 180, 600 };
    private static readonly HashSet<string> TemplatesInspectScenes = new(StringComparer.Ordinal)
    {
        "Strategy",
        "MissionPreparation",
        "Tactical",
    };
    private const int PeriodicInspectIntervalFrames = 300; // ~5s; dumps continue indefinitely while flag set

    private readonly ReplacementCoordinator _replacementCoordinator = new();
    private int _frameInScene;
    private int _frameOfLastPeriodicDump;
    private int _frameOfLastSpawnMonitor;
    private string _currentScene;
    private readonly HashSet<int> _inspectedFrames = new();
    private readonly HashSet<int> _templatesInspectAttempts = new();
    private bool _templatesDumpSucceededThisScene;
    private bool _gateLiveDone;
    private bool _verbsLiveDone;
    private ModHost _modHost;
    private InProcessHookBus _hookBus;
    private TacticalHookPublisher _tacticalHooks;
    private StrategyHookPublisher _strategyHooks;

    public override void OnInitializeMelon()
    {
        SdkAssemblyResolver.Install();
        BindSdkLog();

        LoggerInstance.Msg($"Jiangyu loader v{Info.Version} initialising...");

        var modsDir = Path.Combine(MelonEnvironment.MelonBaseDirectory, "Mods");
        var loadSummary = _replacementCoordinator.LoadBundles(modsDir, LoggerInstance);

        LoggerInstance.Msg(
            $"Resolved {loadSummary.LoadableModCount} loadable mod(s), skipped {loadSummary.BlockedModCount} blocked mod(s), loaded {loadSummary.LoadedBundleCount} bundle(s).");

        GameVersionGate.Check(UnityEngine.Application.unityVersion, ReadCompiledForUnity(modsDir), LoggerInstance.Warning);

        _replacementCoordinator.InstallHarmonyPatches(HarmonyInstance, LoggerInstance);

        if (InjectionGateInspector.IsEnabled())
        {
            InjectionGateInspector.Run("init", live: false, LoggerInstance);
        }

        InitialiseCodeMods(modsDir);
    }

    // Route the SDK's static Jiangyu.Sdk.Log (used by injected handlers and other
    // context-less mod code) into the loader log. Debug is enabled only when the
    // dev file's `debug` toggle is set, so mods can leave Log.Debug calls in
    // without spamming a player's log.
    private void BindSdkLog()
    {
        Jiangyu.Sdk.Log.Bind((level, message) =>
        {
            switch (level)
            {
                case Jiangyu.Sdk.LogLevel.Error: LoggerInstance.Error(message); break;
                case Jiangyu.Sdk.LogLevel.Warn: LoggerInstance.Warning(message); break;
                case Jiangyu.Sdk.LogLevel.Debug: LoggerInstance.Msg($"[debug] {message}"); break;
                default: LoggerInstance.Msg(message); break;
            }
        });

        Jiangyu.Sdk.Log.MinLevel = DevFlags.IsEnabled("debug")
            ? Jiangyu.Sdk.LogLevel.Debug
            : Jiangyu.Sdk.LogLevel.Info;
    }

    private void InitialiseCodeMods(string modsDir)
    {
        try
        {
            var hostLog = new MelonHostLog(LoggerInstance);
            _hookBus = new InProcessHookBus(hostLog);
            ModPatchCoordinator.Initialise(HarmonyInstance);
            _modHost = new ModHost(hostLog, LoaderModContext.Factory(
                hostLog, _hookBus, modsDir,
                assetsProvider: modId => _replacementCoordinator.AssetsFor(modId, hostLog),
                coroutineStart: MelonCoroutines.Start,
                coroutineStop: MelonCoroutines.Stop,
                patchingEnabled: true));
            _tacticalHooks = new TacticalHookPublisher(_hookBus, hostLog);
            _strategyHooks = new StrategyHookPublisher(_hookBus, hostLog);
            StrategyHarmonyPatch.Bus = _hookBus;
            ModStatePersistencePatch.Store = new ModStateStore(_modHost, hostLog);
            _replacementCoordinator.TemplatesApplied = () => _modHost.TemplatesApplied();

            foreach (var modDir in Directory.GetDirectories(modsDir))
            {
                var codeDir = Path.Combine(modDir, "code");
                if (!Directory.Exists(codeDir))
                    continue;

                var modId = ResolveModId(modDir);
                foreach (var dll in Directory.GetFiles(codeDir, "*.dll"))
                {
                    try
                    {
                        var asm = Assembly.LoadFrom(dll);
                        _modHost.Register(asm, modId);
                        JiangyuTypeRegistry.Register(JiangyuTypeCatalog.Scan(asm, modId), hostLog);
                    }
                    catch (Exception ex)
                    {
                        LoggerInstance.Error($"Code mod load failed for {dll}: {ex.Message}");
                    }
                }
            }

            // Injected [JiangyuType] handlers the game constructs have no context of
            // their own; ModContext.For(this) resolves one by the handler's assembly,
            // and the static Log tags each line with the mod that emitted it.
            Jiangyu.Sdk.ModContext.BindResolver(_modHost.ResolveContext);
            Jiangyu.Sdk.Log.BindModResolver(_modHost.ModIdForAssembly);

            _modHost.InitAll();
        }
        catch (Exception ex)
        {
            LoggerInstance.Error($"Code-mod initialisation failed: {ex}");
        }
    }

    // The mod id namespacing every [JiangyuType] as 'modId:Name'. It must match the ns:
    // prefix the compiler baked into the template type= references, which is the manifest
    // Name, so prefer that over the folder name: a renamed Mods/<folder> still resolves
    // its types. Fall back to the folder name when there is no manifest.
    internal static string ResolveModId(string modDir)
    {
        if (Jiangyu.Shared.Bundles.LoaderManifest.TryRead(modDir, out var manifest)
            && !string.IsNullOrWhiteSpace(manifest.Name))
            return manifest.Name;
        return Path.GetFileName(modDir);
    }

    // Each deployed mod's folder name paired with the game Unity version it was
    // compiled against, read from its jiangyu.json. Null when the manifest predates
    // the stamp or was hand-written.
    private static IEnumerable<(string ModId, string CompiledForUnity)> ReadCompiledForUnity(string modsDir)
    {
        if (!Directory.Exists(modsDir))
            yield break;

        foreach (var modDir in Directory.GetDirectories(modsDir))
        {
            if (Jiangyu.Shared.Bundles.LoaderManifest.TryRead(modDir, out var manifest))
                yield return (Path.GetFileName(modDir), manifest.CompiledForUnity);
        }
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        _currentScene = sceneName;
        _frameInScene = 0;
        _frameOfLastPeriodicDump = 0;
        _frameOfLastSpawnMonitor = 0;
        _inspectedFrames.Clear();
        _templatesInspectAttempts.Clear();
        _templatesDumpSucceededThisScene = false;
        _gateLiveDone = false;
        _verbsLiveDone = false;
        _tacticalHooks?.Reset();
        _replacementCoordinator.OnSceneUnloaded();
        InspectionSink.RefreshFlagCache();

        LoggerInstance.Msg($"Scene loaded: {sceneName} ({buildIndex})");

        _modHost?.SceneLoaded(buildIndex, sceneName);

        if (InspectionSink.IsEnabled())
        {
            SceneIdentityInspector.Dump(sceneName + "-t0", buildIndex, LoggerInstance);
        }

        TryApply();
        MelonCoroutines.Start(FollowUpPoll());
    }

    public override void OnUpdate()
    {
        _frameInScene++;

        // Injection gate live phase: retry every ~120 frames in Tactical until an
        // active actor is present, then run once. Gated by the `gate` dev toggle.
        if (!_gateLiveDone && _currentScene == "Tactical"
            && _frameInScene >= 300 && _frameInScene % 300 == 0
            && InjectionGateInspector.IsEnabled())
        {
            _gateLiveDone = InjectionGateInspector.RunLiveIfReady($"Tactical-t{_frameInScene}f", LoggerInstance);
        }

        // Verb-surface live spike: same cadence and active-actor gate as the
        // injection gate, latched once per scene. Gated by the `verbs` dev toggle.
        if (!_verbsLiveDone && _currentScene == "Tactical"
            && _frameInScene >= 300 && _frameInScene % 300 == 0
            && VerbSurfaceProbe.IsEnabled())
        {
            _verbsLiveDone = VerbSurfaceProbe.RunLiveIfReady($"Tactical-t{_frameInScene}f", LoggerInstance);
        }

        if (InspectionSink.IsEnabled() && !string.IsNullOrEmpty(_currentScene))
        {
            foreach (var markFrame in DelayedInspectFrames)
            {
                if (_frameInScene == markFrame && _inspectedFrames.Add(markFrame))
                {
                    SceneIdentityInspector.Dump($"{_currentScene}-t{markFrame}f", 0, LoggerInstance);
                }
            }

            if (!_templatesDumpSucceededThisScene &&
                TemplatesInspectScenes.Contains(_currentScene))
            {
                foreach (var markFrame in TemplatesInspectFrames)
                {
                    if (_frameInScene == markFrame && _templatesInspectAttempts.Add(markFrame))
                    {
                        _templatesDumpSucceededThisScene =
                            TemplateStateInspector.TryDumpTemplatesFromLoader($"{_currentScene}-t{markFrame}f", LoggerInstance);
                    }
                }
            }

            if (_frameInScene >= 600 &&
                _frameInScene - _frameOfLastPeriodicDump >= PeriodicInspectIntervalFrames)
            {
                _frameOfLastPeriodicDump = _frameInScene;
                SceneIdentityInspector.Dump($"{_currentScene}-t{_frameInScene}f", 0, LoggerInstance);
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

        if (_currentScene == "Tactical")
            _tacticalHooks?.EnsureAttached();
        else if (_currentScene == "Strategy")
            _strategyHooks?.EnsureAttached();

        _modHost?.Update();
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
