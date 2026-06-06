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

    /// <summary>Run a game-API verb by name over the bridge. Throws when not connected.</summary>
    [McpTool("jiangyu_verb_run",
        "Run a game-API verb by name against the running game and return its result. 'verb' is \"Class.Method\" (e.g. \"Mission.Actors\", \"Combat.HitChance\", \"Tiles.IsValidTile\"). 'args' is a JSON array matching the verb's parameters; primitives and enum (by name or number) pass directly, and game references use tagged forms: a tile is [x,z] or {tile:[x,z]}, an actor is {actor:\"active\"} or {actor:<index>}, a template is \"id\" or {template:\"id\"}, and a live object returned by an earlier verb is {ref:\"handle\"} (such objects come back carrying a 'handle' field). A verb that mutates game state runs only with mutate:true. Reads need the relevant layer to be live (most tactical verbs need an active Tactical mission).",
        RequiresBridge = true)]
    [McpParam("verb", "string", "The verb to run as \"Class.Method\" (e.g. \"Mission.Actors\", \"Tiles.IsValidTile\").", Required = true)]
    [McpParam("args", "array", "Positional arguments matching the verb's parameters, in order. Primitives and enum (name or number) pass directly; a tile is [x,z] or {tile:[x,z]}, an actor is {actor:\"active\"} or {actor:<index>}, a template is \"id\" or {template:\"id\"}, a live object handed back by an earlier verb is {ref:\"handle\"}. Omit for a no-arg verb.")]
    [McpParam("mutate", "boolean", "Set true to run a verb that mutates game state. A mutating verb is refused without it. Defaults to false.")]
    internal static JsonElement BridgeVerbRun(JsonElement? parameters)
        => Bridge.Request(BridgeMethods.Command, new { name = "verb", args = parameters });

    /// <summary>Capture the game's live UI tree over the bridge. Throws when not connected.</summary>
    [McpTool("jiangyu_ui_capture",
        "Capture the running game's live UI Toolkit tree over the bridge: the active screen and any open dialog, each node's concrete IL2CPP type, name, USS classes and text. Use to find the element to attach injected UI to.",
        RequiresBridge = true)]
    internal static JsonElement BridgeUiCapture(JsonElement? __) => Bridge.Request(BridgeMethods.Command, new { name = "ui" });

    /// <summary>Inspect the running game's current scene over the bridge. Throws when not connected.</summary>
    [McpTool("jiangyu_inspect_scene",
        "Inspect the running game's current scene over the live bridge: every SpriteRenderer, UI Image, SpriteAsset, TextureAsset, SkinnedMeshRenderer, AudioSource and AudioClip live right now, each with its GameObject path. Answers 'what is actually live right now?' when a replacement or template patch is not landing.",
        RequiresBridge = true)]
    internal static JsonElement BridgeInspectScene(JsonElement? __) => Bridge.Request(BridgeMethods.Command, new { name = "scene" });

    /// <summary>Snapshot the running game's registered DataTemplates over the bridge. Throws when not connected.</summary>
    [McpTool("jiangyu_inspect_templates",
        "Snapshot every DataTemplate registered in the running game: identity (id, map key, Unity name, hideFlags), a likely-Jiangyu-clone flag, and each serialised member classified (scalar/bytes/reference/collection/odinBlob/null/unreadable). Use to verify live template state after a clone or patch.",
        RequiresBridge = true)]
    internal static JsonElement BridgeInspectTemplates(JsonElement? __) => Bridge.Request(BridgeMethods.Command, new { name = "templates" });

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
