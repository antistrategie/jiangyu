using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Core.Config;

/// <summary>
/// Per-project Studio config. Stored at {projectRoot}/.jiangyu/config.json.
/// Separate from <see cref="GlobalConfig"/> which holds user-wide settings.
/// </summary>
public sealed class ProjectConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Project-relative (or absolute) directory where exported assets are written when
    /// the user picks the "project" destination in the asset browser. Null means no
    /// project destination configured yet — the UI falls back to the cache default.
    /// </summary>
    [JsonPropertyName("assetExportPath")]
    public string? AssetExportPath { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ProjectConfig FromJson(string json) =>
        JsonSerializer.Deserialize<ProjectConfig>(json, JsonOptions)
        ?? new ProjectConfig();

    public static string ConfigDir(string projectRoot) =>
        Path.Combine(projectRoot, ".jiangyu");

    public static string ConfigPath(string projectRoot) =>
        Path.Combine(ConfigDir(projectRoot), "config.json");

    public static ProjectConfig Load(string projectRoot)
    {
        var path = ConfigPath(projectRoot);
        if (File.Exists(path))
            return FromJson(File.ReadAllText(path));
        return new ProjectConfig();
    }

    public void Save(string projectRoot)
    {
        Directory.CreateDirectory(ConfigDir(projectRoot));
        File.WriteAllText(ConfigPath(projectRoot), ToJson());
    }

    /// <summary>
    /// Resolves <see cref="AssetExportPath"/> against the project root. If the configured
    /// path is absolute, returns it unchanged; if relative, joins against <paramref name="projectRoot"/>.
    /// Returns null if no path is configured.
    /// </summary>
    public string? ResolveAssetExportPath(string projectRoot)
    {
        if (string.IsNullOrEmpty(AssetExportPath))
            return null;
        return Path.IsPathRooted(AssetExportPath)
            ? AssetExportPath
            : Path.Combine(projectRoot, AssetExportPath);
    }
}
