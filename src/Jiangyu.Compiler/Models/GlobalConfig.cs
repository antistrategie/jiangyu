using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Compiler.Models;

public sealed class GlobalConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [JsonPropertyName("game")]
    public string? Game { get; set; }

    [JsonPropertyName("unityEditor")]
    public string? UnityEditor { get; set; }

    [JsonPropertyName("extractedAssets")]
    public string? ExtractedAssets { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static GlobalConfig FromJson(string json) =>
        JsonSerializer.Deserialize<GlobalConfig>(json, JsonOptions)
        ?? new GlobalConfig();

    /// <summary>
    /// Config directory. Respects XDG_CONFIG_HOME on Linux, uses %APPDATA% on Windows.
    /// </summary>
    public static string ConfigDir =>
        Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "jiangyu");

    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    /// <summary>
    /// Default extracted assets cache. Respects XDG_DATA_HOME on Linux, uses %LOCALAPPDATA% on Windows.
    /// </summary>
    public static string DefaultExtractedAssetsDir =>
        Path.Combine(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "jiangyu",
            "extracted");

    /// <summary>
    /// Resolves the extracted assets path: explicit config, or XDG default.
    /// </summary>
    public string GetExtractedAssetsPath() =>
        ExtractedAssets ?? DefaultExtractedAssetsDir;

    /// <summary>
    /// Loads global config from disk. Returns default config if file doesn't exist.
    /// </summary>
    public static GlobalConfig Load()
    {
        if (File.Exists(ConfigPath))
        {
            return FromJson(File.ReadAllText(ConfigPath));
        }
        return new GlobalConfig();
    }

    /// <summary>
    /// Saves global config to disk, creating the directory if needed.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, ToJson());
    }
}
