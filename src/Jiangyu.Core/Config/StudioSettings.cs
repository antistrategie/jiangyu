using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Shared;

namespace Jiangyu.Core.Config;

/// <summary>
/// Studio UI preferences persisted to the filesystem alongside
/// <see cref="GlobalConfig"/>. Previously these lived in the webview's
/// localStorage, but the host binds to a random loopback port on each
/// launch, so localStorage origin is ephemeral — settings were lost on
/// restart. Stored at {GlobalConfig.ConfigDir}/studio.json.
/// </summary>
[RpcType]
public sealed class StudioSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const int EditorFontSizeMin = 8;
    public const int EditorFontSizeMax = 32;
    public const int EditorFontSizeDefault = 14;

    [JsonPropertyName("editorFontSize")]
    public int EditorFontSize { get; set; } = EditorFontSizeDefault;

    public const int UiFontScaleMin = 80;
    public const int UiFontScaleMax = 130;
    public const int UiFontScaleDefault = 100;

    [JsonPropertyName("uiFontScale")]
    public int UiFontScale { get; set; } = UiFontScaleDefault;

    [JsonPropertyName("editorWordWrap")]
    public string EditorWordWrap { get; set; } = "on";

    [JsonPropertyName("sidebarHidden")]
    public bool SidebarHidden { get; set; } = false;

    [JsonPropertyName("sessionRestoreProject")]
    public bool SessionRestoreProject { get; set; } = true;

    [JsonPropertyName("sessionRestoreTabs")]
    public bool SessionRestoreTabs { get; set; } = true;

    [JsonPropertyName("editorKeybindMode")]
    public string EditorKeybindMode { get; set; } = "default";

    [JsonPropertyName("templateEditorMode")]
    public string TemplateEditorMode { get; set; } = "visual";

    [JsonPropertyName("aiEnabled")]
    public bool AiEnabled { get; set; } = false;

    [JsonPropertyName("installedAgents")]
    public List<InstalledAgent> InstalledAgents { get; set; } = [];

    public static string Path => System.IO.Path.Combine(GlobalConfig.ConfigDir, "studio.json");

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static StudioSettings FromJson(string json) =>
        JsonSerializer.Deserialize<StudioSettings>(json, JsonOptions)
        ?? new StudioSettings();

    public static StudioSettings Load()
    {
        if (File.Exists(Path))
            return FromJson(File.ReadAllText(Path));
        return new StudioSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(GlobalConfig.ConfigDir);
        var tmp = Path + ".jiangyu.tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, Path, overwrite: true);
    }
}

/// <summary>
/// An ACP agent the user has added to Studio. The list is the union of
/// agents picked from the registry plus any future built-in. The first
/// installed agent is the one that boots if the user doesn't pick;
/// reorder by removing and reinstalling.
/// </summary>
[RpcType]
public sealed class InstalledAgent
{
    /// <summary>Registry id (e.g. "claude-acp"), or a stable synthetic id.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>Version string at install time. Informational only.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>"npx", "uvx", or "binary".</summary>
    [JsonPropertyName("distribution")]
    public required string Distribution { get; set; }

    /// <summary>Resolvable command (e.g. "bunx" or absolute binary path).</summary>
    [JsonPropertyName("command")]
    public required string Command { get; set; }

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = [];

    /// <summary>Package name for npx/uvx, used to detect updates later.</summary>
    [JsonPropertyName("packageName")]
    public string? PackageName { get; set; }

    /// <summary>Remote SVG URL from the registry. Optional.</summary>
    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; set; }
}
