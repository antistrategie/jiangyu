using Jiangyu.Loader.Diagnostics.InjectionGate;
using Jiangyu.Loader.Diagnostics.UiProbe;
using Jiangyu.Loader.Diagnostics.VerbProbe;
using Jiangyu.Loader.Runtime;
using Jiangyu.Shared.Bridge;
using Jiangyu.Shared.Dev;
using MelonLoader.Utils;

namespace Jiangyu.Loader.Diagnostics;

// The loader's dev surface, merged into the dev loader DLL only. Owns the Studio
// bridge socket and routes its methods to the on-demand probes and inspectors.
// JiangyuMod finds this through the IDevServices seam; the user loader DLL carries
// none of it, so the bridge and every probe are simply absent there.
internal sealed class DevServices : IDevServices
{
    private IDevServicesContext _context;
    private BridgeServer _bridge;

    public void Initialise(IDevServicesContext context)
    {
        _context = context;

        if (InjectionGateInspector.IsEnabled())
        {
            InjectionGateInspector.Run("init", live: false, context.Logger);
        }

        _bridge = new BridgeServer(context.Logger);
        _bridge.On(BridgeMethods.Ping, _ => new { ok = true, version = Jiangyu.Loader.BuildInfo.Version, protocol = BridgeProtocol.Version });
        _bridge.On(BridgeMethods.UiCapture, _ => UiTreeProbe.CaptureCurrent("bridge"));
        _bridge.On(BridgeMethods.InspectScene, _ => SceneIdentityInspector.Capture(_context.CurrentScene, _context.CurrentBuildIndex));
        _bridge.On(BridgeMethods.InspectTemplates, _ => TemplateStateInspector.Capture(_context.CurrentScene));
        _bridge.On(BridgeMethods.GateRun, _ => InjectionGateInspector.Capture(_context.CurrentScene, _context.Logger));
        _bridge.On(BridgeMethods.VerbsRun, _ => VerbSurfaceProbe.Capture(_context.CurrentScene, _context.Logger));
        _bridge.On(BridgeMethods.StrategyRun, _ => StrategyProbe.Capture(_context.Logger));
    }

    public void OnSceneLoaded() => UpdateBridge();

    public void OnUpdate(int frameInScene)
    {
        // Pick up a mid-session bridge toggle (Studio writing the flag) within ~2s,
        // rather than only on scene load.
        if (frameInScene % 120 == 0)
            UpdateBridge();
        _bridge?.Pump();
    }

    // Start or stop the bridge to match the `bridge` flag. Reads the flag fresh so
    // toggling it in Studio mid-session takes effect on the next periodic check,
    // without needing a scene change.
    private void UpdateBridge()
    {
        if (_bridge == null)
            return;
        var enabled = DevFlagFile.IsEnabled(MelonEnvironment.UserDataDirectory, "bridge");
        if (enabled && !_bridge.IsRunning)
            _bridge.Start();
        else if (!enabled && _bridge.IsRunning)
            _bridge.Stop();
    }
}
