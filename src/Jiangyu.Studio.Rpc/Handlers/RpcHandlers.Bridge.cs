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
        "Capture the running game's live UI Toolkit tree over the bridge: the active screen and any open dialog, each node's concrete IL2CPP type, name, USS classes and text. Use to find the element to attach injected UI to. Set 'allPanels' true to also walk every live UIDocument root, reaching UI that lives off the active screen's root (e.g. a second UIDocument like the mission-select map).",
        RequiresBridge = true)]
    [McpParam("allPanels", "boolean", "Also include every live UIDocument root, not just the active screen and open dialog. Use to reach UI on a second UIDocument (e.g. the mission-select map board). Defaults to false.")]
    internal static JsonElement BridgeUiCapture(JsonElement? parameters) => Bridge.Request(BridgeMethods.Command, new { name = "ui", args = parameters });

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

    /// <summary>Drive the record/replay determinism probe over the bridge. Throws when not connected.</summary>
    [McpTool("jiangyu_determinism_probe",
        "Drive the tactical determinism probe against the running game. op 'record' {script} drives a scripted command sequence through the live mission, journaling every command with a state hash at each action barrier; op 'replay' {script, journal} replays the same script and reports the first divergent barrier with an actor-level diff; op 'compare' {a, b} diffs two recorded journals offline (record vs record, e.g. cross-machine or same-session runs) and needs no mission; 'status' and 'abort' manage the active run. Scripts and journals live under <game>/UserData/determinism/ (<name>.script.json). record/replay mutate game state by playing the mission and start from the same loaded save.",
        RequiresBridge = true)]
    [McpParam("op", "string", "record | replay | compare | status | abort.", Required = true)]
    [McpParam("script", "string", "Script name for record/replay (loads <game>/UserData/determinism/<script>.script.json).")]
    [McpParam("journal", "string", "Journal filename for replay (a <script>-record-*.journal.json under determinism/).")]
    [McpParam("a", "string", "First journal filename for compare (under determinism/).")]
    [McpParam("b", "string", "Second journal filename for compare (under determinism/).")]
    internal static JsonElement BridgeDeterminismProbe(JsonElement? parameters)
        => Bridge.Request(BridgeMethods.Command, new { name = "determinism", args = parameters });

    /// <summary>Drive unattended session navigation over the bridge. Throws when not connected.</summary>
    [McpTool("jiangyu_nav",
        "Drive unattended navigation of the running game: op 'load' {save} loads a named save (fixture flow: the game autosaves over latest on operation start, so rigs load their fixture by name); op 'load-latest' loads the most recent save (title-screen Continue); op 'enter-mission' waits for the strategy layer, starts operation 'operation' (index, default 0) with mission 'mission' (index, default 0), opens mission prep and launches into tactical; op 'status' reports the driver phase. Mutates game state: it loads saves and starts missions.",
        RequiresBridge = true)]
    [McpParam("op", "string", "load | load-latest | enter-mission | status.", Required = true)]
    [McpParam("save", "string", "Save name for load (filename, extension optional; determinism runs use the dedicated fixture save).")]
    [McpParam("operation", "integer", "Available-operation index for enter-mission. Defaults to 0.")]
    [McpParam("mission", "integer", "Mission index within the operation for enter-mission. Defaults to 0.")]
    internal static JsonElement BridgeNav(JsonElement? parameters)
        => Bridge.Request(BridgeMethods.Command, new { name = "nav", args = parameters });

    /// <summary>Drive the RNG draw tracer over the bridge. Throws when not connected.</summary>
    [McpTool("jiangyu_rngtrace",
        "Trace the running game's RNG draws at the source: Harmony hooks on UnityEngine.Random (the global frame-shared stream) and Menace's seeded PseudoRandom instances record every draw with sequence, frame, api, arguments, result, and (for PseudoRandom) the owning stream's instance pointer. op 'start' {cap} installs hooks and begins capturing into a ring buffer (default 4096); 'stop' pauses capture; 'dump' {tail} returns the most recent draws; 'status' reports counts. Two processes' traces diff by sequence to the first divergent draw, localising RNG consumers precisely. Hooks cannot see native callers: attribution is by draw order and stream identity.",
        RequiresBridge = true)]
    [McpParam("op", "string", "start | stop | dump | status.", Required = true)]
    [McpParam("cap", "integer", "Ring buffer capacity for start. Defaults to 4096.")]
    [McpParam("tail", "integer", "How many trailing draws dump returns. Defaults to 256.")]
    internal static JsonElement BridgeRngTrace(JsonElement? parameters)
        => Bridge.Request(BridgeMethods.Command, new { name = "rngtrace", args = parameters });

    /// <summary>Drive the multiplayer net probe over the bridge. Throws when not connected.</summary>
    [McpTool("jiangyu_net",
        "Drive the multiplayer transport and lobby probe against the running game. op 'loopback' runs the in-process transport pair self-test (handshake to Ready, chat echo, and a deliberate mod-set mismatch) synchronously with no Steam dependency; 'selftest' runs the same handshake and echo against the local SteamID over the real Steam messages transport (poll status for the verdict); 'host' creates a friends-only Steam lobby awaiting a second member; 'join' {lobby} joins a lobby by id; 'send' {text} sends a chat line on the ready session; 'echo' {enabled} toggles auto-echo of received chat; 'leave' tears the session down; 'status' reports steam liveness, lobby membership, session phase, reject differences and the chat transcript. SteamID64 and lobby ids travel as decimal strings.",
        RequiresBridge = true)]
    [McpParam("op", "string", "status | loopback | selftest | host | join | leave | send | echo.", Required = true)]
    [McpParam("lobby", "string", "Lobby id for join, as the decimal string from the host's status.")]
    [McpParam("text", "string", "Chat line for send.")]
    [McpParam("enabled", "boolean", "Echo toggle for echo. Defaults to false.")]
    internal static JsonElement BridgeNet(JsonElement? parameters)
        => Bridge.Request(BridgeMethods.Command, new { name = "net", args = parameters });

    private static bool IsBridgeEnabled()
    {
        var dir = UserDataDir();
        return dir is not null && DevFlagFile.IsEnabled(dir, BridgeFlagName);
    }

    private static string? UserDataDir()
    {
        var (gameDir, _) = GlobalConfig.ResolveGamePath(GlobalConfig.Load());
        return gameDir is null ? null : Path.Combine(gameDir, "UserData");
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
    /// <summary>A live capture of the game's UI tree: the active screen, any open dialog, and the live tooltip overlay.</summary>
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

        // Optional: the live tooltips on the overlay panel (pinned tooltips and nested children),
        // captured from UIManager's tooltip stack. Absent in captures from a loader that predates
        // tooltip capture, so it is not required.
        [JsonPropertyName("tooltips")]
        public List<UiTooltip>? Tooltips { get; set; }

        // Optional: every live UIDocument root, captured only when the request asked for
        // allPanels. Surfaces UI that lives off the active screen's root (a second
        // UIDocument, an overlay panel). Absent from a default capture or an older loader.
        [JsonPropertyName("panels")]
        public List<UiPanel>? Panels { get; set; }
    }

    /// <summary>A live UIDocument root panel: its GameObject path, sorting order, and content tree.</summary>
    [RpcType]
    internal sealed class UiPanel
    {
        [JsonPropertyName("gameObject")]
        public required string? GameObject { get; set; }

        [JsonPropertyName("sortingOrder")]
        public required float SortingOrder { get; set; }

        [JsonPropertyName("tree")]
        public required UiNode? Tree { get; set; }
    }

    /// <summary>A live tooltip from the overlay: its id, pin state, the element that triggered it, and its content tree.</summary>
    [RpcType]
    internal sealed class UiTooltip
    {
        [JsonPropertyName("tooltipId")]
        public required string? TooltipId { get; set; }

        [JsonPropertyName("isPinned")]
        public required bool IsPinned { get; set; }

        [JsonPropertyName("trigger")]
        public required UiNode? Trigger { get; set; }

        [JsonPropertyName("tree")]
        public required UiNode? Tree { get; set; }
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

        // Computed style snapshot from the probe: geometry (x/y/w/h), colours (rgba
        // strings), fonts, borders, layout. Values are strings or numbers.
        [JsonPropertyName("style")]
        public required Dictionary<string, object>? Style { get; set; }
    }
}
