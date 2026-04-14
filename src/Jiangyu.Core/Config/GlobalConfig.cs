using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Core.Config;

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

    /// <summary>
    /// Asset pipeline cache root. Contains:
    ///   asset-index.json     — searchable asset catalogue (from 'assets index')
    ///   index-manifest.json  — cache validity metadata
    ///   template-index.json  — searchable template catalogue (from 'templates index')
    ///   template-index-manifest.json — template cache validity metadata
    ///   exports/             — raw exported assets (from 'assets export')
    /// </summary>
    [JsonPropertyName("cache")]
    public string? Cache { get; set; }

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
    /// Default cache directory. Respects XDG_DATA_HOME on Linux, uses %LOCALAPPDATA% on Windows.
    /// </summary>
    public static string DefaultCacheDir =>
        Path.Combine(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "jiangyu",
            "cache");

    /// <summary>
    /// Resolves the cache path: explicit config, or platform default.
    /// </summary>
    public string GetCachePath() =>
        Cache ?? DefaultCacheDir;

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

    /// <summary>
    /// Resolves the game data directory (_Data) from the global config game path.
    /// Returns null with an error message if not configured or not found.
    /// </summary>
    public static (string? gameDataPath, string? error) ResolveGameDataPath()
    {
        var config = Load();
        if (string.IsNullOrEmpty(config.Game))
        {
            return (null, $"Error: game path not set. Edit {ConfigPath}\n\nExample:\n  {{\n    \"game\": \"~/.steam/steam/steamapps/common/Menace\"\n  }}");
        }

        var gamePath = ExpandHome(config.Game);
        if (!Directory.Exists(gamePath))
        {
            return (null, $"Error: game directory not found: {gamePath}");
        }

        foreach (var dir in Directory.EnumerateDirectories(gamePath))
        {
            if (Path.GetFileName(dir).EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            {
                return (dir, null);
            }
        }

        return (null, $"Error: could not find game data directory in: {gamePath}\nExpected a directory ending in _Data (e.g. Menace_Data)");
    }

    internal static string ExpandHome(string path)
    {
        if (path.StartsWith('~'))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        }
        return path;
    }
}
