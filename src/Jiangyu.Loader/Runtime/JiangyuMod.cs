using System.Reflection;
using Jiangyu.Loader.Diagnostics;
using Jiangyu.Loader.Logging;
using Jiangyu.Loader.Sdk;
using Jiangyu.Loader.Sdk.Hooks;
using Jiangyu.Loader.Sdk.Patches;
using Jiangyu.Loader.Sdk.State;
using Jiangyu.Loader.Sdk.Types;
using Jiangyu.Shared.Bundles;
using MelonLoader;
using MelonLoader.Utils;

[assembly: MelonInfo(typeof(Jiangyu.Loader.Runtime.JiangyuMod), "Jiangyu", Jiangyu.Loader.BuildInfo.Version, "ΔNTISTRATÉGIE")]
[assembly: MelonGame("Overhype Studios", "Menace")]
[assembly: MelonPriority(-100)]

namespace Jiangyu.Loader.Runtime;

// MelonLoader entry point. Owns the loader's subsystems and forwards the MelonLoader
// lifecycle (init, scene load) to each. The loader runs no per-frame OnUpdate: replacement
// application, UI re-injection, and hook attachment are event-driven (Harmony postfixes on
// the game's own methods), and dev inspection is served on demand over the Studio bridge.
// This class wires and sequences them.
public class JiangyuMod : MelonMod, IDevServicesContext
{
    private readonly ReplacementCoordinator _replacementCoordinator = new();
    private readonly ReplacementScheduler _replacement;
    private string _currentScene;
    private int _currentBuildIndex;
    private IDevServices _dev;
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

        // The stamps come off the plan, which parsed each manifest during discovery, so no
        // jiangyu.json is read twice. A gate names the mod by its id, which is what the rest
        // of the loader calls it. The bundle loader has already logged the folder it is in.
        var mods = _replacementCoordinator.LoadableMods;
        GameVersionGate.Check(
            UnityEngine.Application.unityVersion,
            VersionStamps(mods, mod => mod.CompiledForUnity),
            LoggerInstance.Warning);
        JiangyuVersionGate.Check(
            BuildInfo.Version,
            VersionStamps(mods, mod => mod.CompiledForJiangyu),
            LoggerInstance.Warning);

        _replacementCoordinator.InstallHarmonyPatches(HarmonyInstance, LoggerInstance);

        InitialiseCodeMods(modsDir, mods);

        // The dev surface (Studio bridge + probes) is merged into the dev loader DLL
        // only. The user loader DLL has no implementation to discover, so this is a
        // no-op and none of that code is present to run. The surface is optional, so a
        // failure bringing it up degrades to "no dev surface" rather than bricking the
        // loader.
        try
        {
            _dev = DiscoverDevServices();
            _dev?.Initialise(this);
        }
        catch (Exception ex)
        {
            LoggerInstance.Warning($"Dev surface failed to initialise, continuing without it: {ex.Message}");
            _dev = null;
        }
    }

    // Find the IDevServices implementation merged into this assembly. Present in the
    // dev loader DLL (Jiangyu.Loader.Diagnostics is ILRepacked in), absent in the user
    // loader DLL, where this returns null and the seam stays dormant.
    private static IDevServices DiscoverDevServices()
    {
        var type = Assembly.GetExecutingAssembly().GetType("Jiangyu.Loader.Diagnostics.DevServices");
        return type == null ? null : (IDevServices)Activator.CreateInstance(type, nonPublic: true);
    }

    MelonLogger.Instance IDevServicesContext.Logger => LoggerInstance;
    string IDevServicesContext.CurrentScene => _currentScene;
    int IDevServicesContext.CurrentBuildIndex => _currentBuildIndex;
    System.Collections.Generic.IEnumerable<System.Reflection.Assembly> IDevServicesContext.ModAssemblies
        => _modHost?.ModAssemblies ?? System.Array.Empty<System.Reflection.Assembly>();

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

    private void InitialiseCodeMods(string modsDir, IReadOnlyList<DiscoveredMod> mods)
    {
        try
        {
            var hostLog = new MelonHostLog(LoggerInstance);
            _hookBus = new InProcessHookBus(hostLog);
            ModPatchCoordinator.Initialise(HarmonyInstance);

            // Game.Input.Hotkeys: one loader-owned per-frame coroutine polls input and fans
            // each press out to mod-registered handlers, so a mod never writes a frame loop.
            // Registrations are grouped per mod (by the handler's assembly), so the loader drops
            // them on that mod's unload alongside its coroutines and patches.
            var hotkeyDispatch = new Sdk.Input.HotkeyDispatch();
            var hotkeyRegistry = new Sdk.Input.HotkeyRegistry(hotkeyDispatch, asm => _modHost?.ModIdForAssembly(asm));

            // A mod is identified by its manifest name, and its folder can be called
            // anything, so the two only coincide by convention. The plan already knows both,
            // and this is the one place that pairs them. Everything downstream takes the id.
            // Duplicate names are blocked before a mod becomes loadable, so ids are unique.
            var modDirsById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var mod in mods)
                modDirsById[mod.Name] = mod.DirectoryPath;

            _modHost = new ModHost(hostLog, LoaderModContext.Factory(
                hostLog, _hookBus, modsDir,
                assetsProvider: modId => _replacementCoordinator.AssetsFor(modId, hostLog),
                coroutineStart: MelonCoroutines.Start,
                coroutineStop: MelonCoroutines.Stop,
                patchingEnabled: true,
                modFolderResolver: modId => modDirsById.TryGetValue(modId, out var dir) ? dir : null),
                clearModHotkeys: hotkeyRegistry.ClearMod);
            _tacticalHooks = new TacticalHookPublisher(_hookBus, hostLog);
            _strategyHooks = new StrategyHookPublisher(_hookBus, hostLog);
            StrategyHarmonyPatch.Bus = _hookBus;
            TacticalManagerStartPatch.Publisher = _tacticalHooks;
            StrategyAttachPatch.Publisher = _strategyHooks;
            ModStatePersistencePatch.Store = new ModStateStore(_modHost, hostLog);
            _replacementCoordinator.TemplatesApplied = () => _modHost.TemplatesApplied();

            foreach (var mod in mods)
            {
                var codeDir = Path.Combine(mod.DirectoryPath, CompiledLayout.CodeDirName);
                if (!Directory.Exists(codeDir))
                    continue;

                var modId = mod.Name;
                // Load every code DLL first, then register the mod's systems together, so
                // [DependsOn] orders across a multi-DLL mod rather than within one DLL.
                var assemblies = new List<Assembly>();
                foreach (var dll in Directory.GetFiles(codeDir, "*.dll").OrderBy(path => path, StringComparer.Ordinal))
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
            Jiangyu.Game.Ui.UI.BindUxmlResolver((assembly, name) =>
            {
                var modId = _modHost.ModIdForAssembly(assembly);
                if (string.IsNullOrEmpty(modId))
                    return null;
                return _replacementCoordinator.AssetsFor(modId, hostLog)
                    ?.Load<UnityEngine.UIElements.VisualTreeAsset>(name);
            });

            // Bound before InitAll so a system's OnInit can register hotkeys. The dispatch and
            // registry were created above so the registry's ClearMod could be handed to the host.
            Jiangyu.Game.Input.Hotkeys.BindRegistrar(hotkeyRegistry);
            MelonCoroutines.Start(Sdk.Input.HotkeyPump.Poll(
                hotkeyDispatch,
                ex => LoggerInstance.Error($"Hotkey handler threw and was removed: {ex.GetType().Name}: {ex.Message}")));

            _modHost.InitAll();
        }
        catch (Exception ex)
        {
            LoggerInstance.Error($"Code-mod initialisation failed: {ex}");
        }
    }

    // Each loadable mod's id paired with one of its compile-time stamps, in the shape both
    // version gates take.
    private static IEnumerable<(string ModId, string Stamp)> VersionStamps(
        IReadOnlyList<DiscoveredMod> mods,
        Func<DiscoveredMod, string> stamp)
    {
        foreach (var mod in mods)
            yield return (mod.Name, stamp(mod));
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        _currentScene = sceneName;
        _currentBuildIndex = buildIndex;

        // Re-read the dev flags for the new scene (DevFlags caches the file, so per-frame
        // gate checks stay dict lookups) and re-sync the SDK logger's level to the debug
        // gate, so a mid-session toggle takes effect here for both loader and SDK logging.
        DevFlags.Refresh();
        LoaderDebug.SyncSdkLog();

        _replacement.Reset();
        _tacticalHooks?.Reset();
        _dev?.OnSceneLoaded();

        LoggerInstance.Msg($"Scene loaded: {sceneName} ({buildIndex})");

        _modHost?.SceneLoaded(buildIndex, sceneName);

        _replacement.Apply(LoggerInstance);
        MelonCoroutines.Start(_replacement.FollowUpPoll(LoggerInstance));
    }
}
