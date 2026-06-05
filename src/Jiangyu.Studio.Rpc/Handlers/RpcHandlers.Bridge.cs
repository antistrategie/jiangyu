using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Config;
using Jiangyu.Core.Rpc;
using Jiangyu.Shared.Bridge;
using Jiangyu.Shared.Dev;

namespace Jiangyu.Studio.Rpc;

public static partial class RpcHandlers
{
    // The single source of truth for "bridge enabled" is the `bridge` toggle in the
    // game's jiangyu-flags file (read/written through the shared DevFlagFile grammar):
    // the loader opens its socket only when that flag is set. So there is no separate
    // Studio setting to keep in sync.
    private const string BridgeFlagName = "bridge";

    private static readonly GameBridgeClient Bridge = new();

    /// <summary>Whether the bridge is enabled (flag set) and currently connected.</summary>
    internal static JsonElement BridgeStatus(JsonElement? __)
    {
        var enabled = IsBridgeEnabled();
        var connected = false;
        if (enabled)
            connected = Bridge.TryConnect();
        else
            Bridge.Disconnect();
        return JsonSerializer.SerializeToElement(new BridgeStatusResult { Enabled = enabled, Connected = connected });
    }

    /// <summary>Enable or disable the bridge: write the game flag and connect or disconnect.</summary>
    internal static JsonElement BridgeSetEnabled(JsonElement? parameters)
    {
        var enabled = parameters is { } p && p.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
        var dir = UserDataDir();
        if (dir is not null)
            DevFlagFile.Set(dir, BridgeFlagName, enabled);

        var connected = false;
        if (enabled)
            connected = Bridge.TryConnect();
        else
            Bridge.Disconnect();
        return JsonSerializer.SerializeToElement(new BridgeStatusResult { Enabled = enabled, Connected = connected });
    }

    /// <summary>Capture the game's live UI tree over the bridge. Throws when not connected.</summary>
    [McpTool("jiangyu_ui_capture",
        "Capture the running game's live UI Toolkit tree over the bridge: the active screen and any open dialog, each node's concrete IL2CPP type, name, USS classes and text. Use to find the element to attach injected UI to.",
        RequiresBridge = true)]
    internal static JsonElement BridgeUiCapture(JsonElement? __) => Bridge.Request(BridgeMethods.UiCapture);

    /// <summary>Inspect the running game's current scene over the bridge. Throws when not connected.</summary>
    [McpTool("jiangyu_inspect_scene",
        "Inspect the running game's current scene over the live bridge: every SpriteRenderer, UI Image, SpriteAsset, TextureAsset, SkinnedMeshRenderer, AudioSource and AudioClip live right now, each with its GameObject path. Answers 'what is actually live right now?' when a replacement or template patch is not landing.",
        RequiresBridge = true)]
    internal static JsonElement BridgeInspectScene(JsonElement? __) => Bridge.Request(BridgeMethods.InspectScene);

    /// <summary>Snapshot the running game's registered DataTemplates over the bridge. Throws when not connected.</summary>
    [McpTool("jiangyu_inspect_templates",
        "Snapshot every DataTemplate registered in the running game: identity (id, map key, Unity name, hideFlags), a likely-Jiangyu-clone flag, and each serialised member classified (scalar/bytes/reference/collection/odinBlob/null/unreadable). Use to verify live template state after a clone or patch.",
        RequiresBridge = true)]
    internal static JsonElement BridgeInspectTemplates(JsonElement? __) => Bridge.Request(BridgeMethods.InspectTemplates);

    /// <summary>Run the injection-gate diagnostic against the running game over the bridge. Throws when not connected.</summary>
    [McpTool("jiangyu_gate_run",
        "Run the injection-gate diagnostic against the running game and return its report: type registration, runtime assignability, blank-template allocation, Odin polymorphic survival, and (when an actor is live in a Tactical mission) game-dispatch-to-injected-handler plus the opt-in damage-clamp check.",
        RequiresBridge = true)]
    internal static JsonElement BridgeGateRun(JsonElement? __) => Bridge.Request(BridgeMethods.GateRun);

    /// <summary>Run the game-API verb-surface probe against the running game over the bridge. Throws when not connected.</summary>
    [McpTool("jiangyu_verbs_run",
        "Run the game-API verb-surface probe against the running game and return its report: tile/faction/actor reads, line-of-sight, hit-chance, path queries, the movement-range task, and (behind the verbs-spawn opt-in) spawn/despawn behaviour. Needs an active actor in a live Tactical mission.",
        RequiresBridge = true)]
    internal static JsonElement BridgeVerbsRun(JsonElement? __) => Bridge.Request(BridgeMethods.VerbsRun);

    /// <summary>Run the strategy-layer (campaign) probe against the running game over the bridge. Throws when not connected.</summary>
    [McpTool("jiangyu_strategy_run",
        "Run the strategy-layer (campaign) probe against the running game and return its report: campaign resources (OCI components, promotion points, intelligence, authority), a net-zero ChangeVar round-trip that validates the resource-mutation path without a lasting change, and roster counts (available units, hired and hirable leaders). Needs the game in the Strategy layer (between missions), not a tactical mission.",
        RequiresBridge = true)]
    internal static JsonElement BridgeStrategyRun(JsonElement? __) => Bridge.Request(BridgeMethods.StrategyRun);

    private static bool IsBridgeEnabled()
    {
        var dir = UserDataDir();
        return dir is not null && DevFlagFile.IsEnabled(dir, BridgeFlagName);
    }

    private static string? UserDataDir()
    {
        var (gameDir, _) = GlobalConfig.ResolveGamePath(GlobalConfig.Load());
        return gameDir is null ? null : System.IO.Path.Combine(gameDir, "UserData");
    }

    [RpcType]
    internal sealed class BridgeStatusResult
    {
        [JsonPropertyName("enabled")]
        public required bool Enabled { get; set; }

        [JsonPropertyName("connected")]
        public required bool Connected { get; set; }
    }

    // UiDump/UiNode define the ui.capture wire contract. The loader emits this shape (camelCase)
    // and BridgeUiCapture passes it through verbatim; [RpcType] generates the matching TS in the UI.
    // Mirror of the loader's UiTreeProbe dump (Jiangyu.Loader/Diagnostics/UiProbe/UiTreeProbe.cs).
    /// <summary>A live capture of the game's UI tree: the active screen and any open dialog.</summary>
    [RpcType]
    internal sealed class UiDump
    {
        [JsonPropertyName("activeScreen")]
        public required string? ActiveScreen { get; set; }

        [JsonPropertyName("currentDialog")]
        public required string? CurrentDialog { get; set; }

        [JsonPropertyName("nodeCount")]
        public required int NodeCount { get; set; }

        [JsonPropertyName("truncated")]
        public required bool Truncated { get; set; }

        [JsonPropertyName("screenTree")]
        public required UiNode? ScreenTree { get; set; }

        [JsonPropertyName("dialogTree")]
        public required UiNode? DialogTree { get; set; }
    }

    /// <summary>A node in a captured UI tree.</summary>
    [RpcType]
    internal sealed class UiNode
    {
        [JsonPropertyName("type")]
        public required string? Type { get; set; }

        [JsonPropertyName("name")]
        public required string? Name { get; set; }

        [JsonPropertyName("text")]
        public required string? Text { get; set; }

        [JsonPropertyName("classes")]
        public required List<string>? Classes { get; set; }

        [JsonPropertyName("children")]
        public required List<UiNode>? Children { get; set; }
    }
}
