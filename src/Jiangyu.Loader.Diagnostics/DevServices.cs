using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using Jiangyu.Loader.Diagnostics.UiProbe;
using Jiangyu.Loader.Runtime;
using Jiangyu.Shared.Bridge;
using Jiangyu.Shared.Dev;
using MelonLoader;
using MelonLoader.Utils;

namespace Jiangyu.Loader.Diagnostics;

// The loader's dev surface, merged into the dev loader DLL only. Owns the Studio bridge
// socket and a registry of named commands routed over one `command` method: the generic
// verb runner plus the on-demand inspectors. JiangyuMod finds this through the
// IDevServices seam; the user loader DLL carries none of it.
internal sealed class DevServices : IDevServices
{
    private IDevServicesContext _context;
    private BridgeServer _bridge;
    private Dictionary<string, Func<JsonElement, object>> _commands;
    private bool _pumpStarted;

    public void Initialise(IDevServicesContext context)
    {
        _context = context;

        // name -> handler. The verb runner takes the command's args; the inspectors read
        // live scene state from the context and ignore them.
        _commands = new Dictionary<string, Func<JsonElement, object>>(StringComparer.Ordinal)
        {
            ["verb"] = args => VerbRunner.Run(args, _context.Logger),
            ["ui"] = _ => UiTreeProbe.CaptureCurrent(_context.CurrentScene),
            ["scene"] = _ => SceneIdentityInspector.Capture(_context.CurrentScene, _context.CurrentBuildIndex),
            ["templates"] = _ => TemplateStateInspector.Capture(_context.CurrentScene),
        };

        _bridge = new BridgeServer(context.Logger);
        _bridge.On(BridgeMethods.Ping, _ => new { ok = true, version = Jiangyu.Loader.BuildInfo.Version, protocol = BridgeProtocol.Version });
        _bridge.On(BridgeMethods.Command, HandleCommand);
    }

    // A `command` request is { name, args }: dispatch by name to the registry, passing
    // the args sub-payload (the verb runner's {verb,args,mutate}; inspectors ignore it).
    private object HandleCommand(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object
            || !request.TryGetProperty("name", out var n) || n.GetString() is not { } name)
            return new { error = "missing command 'name'" };
        if (!_commands.TryGetValue(name, out var handler))
            return new { error = $"unknown command '{name}'" };

        var args = request.TryGetProperty("args", out var a) ? a : default;
        return handler(args);
    }

    public void OnSceneLoaded()
    {
        // Handles name live objects from the scene that just unloaded. Drop them so a
        // {ref:"..."} can never resolve to a stale instance.
        ObjectHandles.Clear();
        UpdateBridge();

        // Start the main-thread request pump once, on the first scene load. MelonLoader's
        // coroutine runner is ready by now (it is not at OnInitializeMelon time), and a
        // self-contained loop keeps the pump off the loader's frame tick.
        if (!_pumpStarted)
        {
            _pumpStarted = true;
            MelonCoroutines.Start(PumpLoop());
        }
    }

    // Drain the bridge's queued request handlers on the main thread each frame (game and
    // UI APIs are main-thread-only), and pick up a mid-session bridge toggle without
    // waiting for a scene change. Runs for the session: the dev loader lives until exit.
    private IEnumerator PumpLoop()
    {
        var frame = 0;
        while (true)
        {
            if (frame % 120 == 0)
                UpdateBridge();
            _bridge?.Pump();
            frame++;
            yield return null;
        }
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
