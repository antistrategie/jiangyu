using System.Reflection;
using Jiangyu.Loader.Bridge;
using Jiangyu.Loader.Diagnostics;
using Jiangyu.Loader.Diagnostics.InjectionGate;
using Jiangyu.Loader.Diagnostics.UiProbe;
using Jiangyu.Loader.Diagnostics.VerbProbe;
using Jiangyu.Loader.Logging;
using Jiangyu.Loader.Sdk;
using Jiangyu.Loader.Sdk.Hooks;
using Jiangyu.Loader.Sdk.Patches;
using Jiangyu.Loader.Sdk.State;
using Jiangyu.Loader.Sdk.Types;
using Jiangyu.Shared.Bridge;
using MelonLoader;
using MelonLoader.Utils;

[assembly: MelonInfo(typeof(Jiangyu.Loader.Runtime.JiangyuMod), "Jiangyu", Jiangyu.Loader.BuildInfo.Version, "antistrategie")]
[assembly: MelonGame("Overhype Studios", "Menace")]

namespace Jiangyu.Loader.Runtime;

// MelonLoader entry point. Owns the per-scene frame clock and the loader's subsystems,
// and forwards the MelonLoader lifecycle (init, scene load, update) to each. The
// per-scene and per-frame work lives in the drivers (replacement scheduling, UI
// re-injection); dev inspection is served on demand over the Studio bridge. This class
// wires and sequences them.
public class JiangyuMod : MelonMod
{
    private readonly ReplacementCoordinator _replacementCoordinator = new();
    private readonly ReplacementScheduler _replacement;
    private readonly UiInjectionDriver _ui = new();
    private int _frameInScene;
    private string _currentScene;
    private int _currentBuildIndex;
    private BridgeServer _bridge;
    private ModHost _modHost;
    private InProcessHookBus _hookBus;
    private TacticalHookPublisher _tacticalHooks;
    private StrategyHookPublisher _strategyHooks;

    // A field initialiser cannot reference _replacementCoordinator, so wire the scheduler
    // here. MelonLoader constructs the mod through this parameterless constructor.
    public JiangyuMod() => _replacement = new ReplacementScheduler(_replacementCoordinator);

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
        InitialiseBridge();
    }

    // The Studio bridge: a localhost socket Studio connects to, opened only while the
    // `bridge` dev flag is set (Studio writes that flag when its toggle is on). Handlers
    // run on the main thread via Pump. Start/stop is driven by the flag on scene load.
    private void InitialiseBridge()
    {
        _bridge = new BridgeServer(LoggerInstance);
        _bridge.On(BridgeMethods.Ping, _ => new { ok = true, version = Info.Version, protocol = BridgeProtocol.Version });
        _bridge.On(BridgeMethods.UiCapture, _ => UiTreeProbe.CaptureCurrent("bridge"));
        _bridge.On(BridgeMethods.InspectScene, _ => SceneIdentityInspector.Capture(_currentScene, _currentBuildIndex));
        _bridge.On(BridgeMethods.InspectTemplates, _ => TemplateStateInspector.Capture(_currentScene));
        _bridge.On(BridgeMethods.GateRun, _ => InjectionGateInspector.Capture(_currentScene, LoggerInstance));
        _bridge.On(BridgeMethods.VerbsRun, _ => VerbSurfaceProbe.Capture(_currentScene, LoggerInstance));
        _bridge.On(BridgeMethods.StrategyRun, _ => StrategyProbe.Capture(LoggerInstance));
    }

    // Start/stop the bridge to match the `bridge` flag. Reads the flag fresh (not the
    // scene-cached DevFlags) so toggling it in Studio mid-session takes effect on the
    // next periodic check, without needing a scene change.
    private void UpdateBridge()
    {
        if (_bridge == null)
            return;
        var enabled = Jiangyu.Shared.Dev.DevFlagFile.IsEnabled(MelonEnvironment.UserDataDirectory, "bridge");
        if (enabled && !_bridge.IsRunning)
            _bridge.Start();
        else if (!enabled && _bridge.IsRunning)
            _bridge.Stop();
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
                case Jiangyu.Sdk.LogLevel.Debug: LoggerInstance.Msg(LoaderDebug.Decorate(message)); break;
                default: LoggerInstance.Msg(message); break;
            }
        });

        LoaderDebug.SyncSdkLog();
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
                // Load every code DLL first, then register the mod's systems together, so
                // [DependsOn] orders across a multi-DLL mod rather than within one DLL.
                var assemblies = new List<Assembly>();
                foreach (var dll in Directory.GetFiles(codeDir, "*.dll"))
                {
                    try
                    {
                        var asm = Assembly.LoadFrom(dll);
                        assemblies.Add(asm);
                        JiangyuTypeRegistry.Register(JiangyuTypeCatalog.Scan(asm, modId), hostLog);
                    }
                    catch (Exception ex)
                    {
                        LoggerInstance.Error($"Code mod load failed for {dll}: {ex.Message}");
                    }
                }

                if (assemblies.Count > 0)
                {
                    try
                    {
                        _modHost.Register(assemblies, modId);
                    }
                    catch (Exception ex)
                    {
                        LoggerInstance.Error($"Code mod system registration failed for {modId}: {ex.Message}");
                    }
                }
            }

            // Injected [JiangyuType] handlers the game constructs have no context of
            // their own; ModContext.For(this) resolves one by the handler's assembly,
            // and the static Log tags each line with the mod that emitted it.
            Jiangyu.Sdk.ModContext.BindResolver(_modHost.ResolveContext);
            Jiangyu.Sdk.Log.BindModResolver(_modHost.ModIdForAssembly);

            // Game.UI resolves a UXML name against the calling mod's own bundles.
            Jiangyu.Game.UI.BindUxmlResolver((assembly, name) =>
            {
                var modId = _modHost.ModIdForAssembly(assembly);
                if (string.IsNullOrEmpty(modId))
                    return null;
                return _replacementCoordinator.AssetsFor(modId, hostLog)
                    ?.Load<UnityEngine.UIElements.VisualTreeAsset>(name);
            });

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
        _currentBuildIndex = buildIndex;
        _frameInScene = 0;

        // Re-read the dev flags for the new scene (DevFlags caches the file, so per-frame
        // gate checks stay dict lookups) and re-sync the SDK logger's level to the debug
        // gate, so a mid-session toggle takes effect here for both loader and SDK logging.
        DevFlags.Refresh();
        LoaderDebug.SyncSdkLog();

        _replacement.Reset();
        _ui.OnSceneLoaded();
        _tacticalHooks?.Reset();
        UpdateBridge();

        LoggerInstance.Msg($"Scene loaded: {sceneName} ({buildIndex})");

        _modHost?.SceneLoaded(buildIndex, sceneName);

        _replacement.Apply(LoggerInstance);
        MelonCoroutines.Start(_replacement.FollowUpPoll(LoggerInstance));
    }

    public override void OnUpdate()
    {
        _frameInScene++;

        _ui.Drive();

        // Pick up a mid-session bridge toggle (Studio writing the flag) within ~2s,
        // rather than only on scene load.
        if (_frameInScene % 120 == 0)
            UpdateBridge();
        _bridge?.Pump();

        _replacement.Tick(_frameInScene, LoggerInstance);

        if (_currentScene == "Tactical")
            _tacticalHooks?.EnsureAttached();
        else if (_currentScene == "Strategy")
            _strategyHooks?.EnsureAttached();

        _modHost?.Update();
    }
}
