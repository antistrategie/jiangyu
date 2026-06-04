using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Shared;

namespace Jiangyu.Core.Config;

public sealed class GlobalConfig
{
    [JsonPropertyName("game")]
    public string? Game { get; set; }

    [JsonPropertyName("unityEditor")]
    public string? UnityEditor { get; set; }

    /// <summary>
    /// Directory containing <c>Jiangyu.Sdk.dll</c> that mod <c>code/</c> projects
    /// reference. When unset it is resolved next to the running CLI, then from the
    /// dev source tree. See <see cref="ResolveSdkDir"/>.
    /// </summary>
    [JsonPropertyName("sdk")]
    public string? Sdk { get; set; }

    /// <summary>
    /// Asset pipeline cache root. Contains:
    ///   asset-index.json             — searchable asset catalogue (from 'assets index')
    ///   asset-index-manifest.json    — asset cache validity metadata
    ///   template-index.json          — searchable template catalogue (from 'templates index')
    ///   template-index-manifest.json — template cache validity metadata
    ///   exports/             — raw exported assets (from 'assets export')
    /// </summary>
    [JsonPropertyName("cache")]
    public string? Cache { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions.PrettyCamel);

    public static GlobalConfig FromJson(string json) =>
        JsonSerializer.Deserialize<GlobalConfig>(json, JsonOptions.PrettyCamel)
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
    /// Falls back to well-known Steam install locations if not configured.
    /// </summary>
    public static (string? gameDataPath, string? error) ResolveGameDataPath()
    {
        return ResolveGameDataPath(Load());
    }

    /// <summary>
    /// Resolves the game data directory (_Data) from the provided config's game path.
    /// Falls back to well-known Steam install locations if not configured.
    /// </summary>
    public static (string? gameDataPath, string? error) ResolveGameDataPath(GlobalConfig config)
    {
        var (gamePath, error) = ResolveGamePath(config);
        if (gamePath is null)
            return (null, error);

        foreach (var dir in Directory.EnumerateDirectories(gamePath))
        {
            if (Path.GetFileName(dir).EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            {
                return (dir, null);
            }
        }

        return (null, $"Error: could not find game data directory in: {gamePath}\nExpected a directory ending in _Data (e.g. Menace_Data)");
    }

    /// <summary>
    /// Resolves the game install root (the directory containing the <c>_Data</c>
    /// folder and <c>MelonLoader/</c>), used to inject <c>GameDir</c> into a mod's
    /// <c>code/</c> build. Falls back to well-known Steam locations.
    /// </summary>
    public static (string? gamePath, string? error) ResolveGamePath(GlobalConfig config)
    {
        var gamePath = !string.IsNullOrEmpty(config.Game)
            ? ExpandHome(config.Game)
            : DiscoverGamePath();

        if (gamePath is null)
            return (null, $"Error: game path not found. Set it in {ConfigPath}\n\nExample:\n  {{\n    \"game\": \"~/.steam/steam/steamapps/common/Menace\"\n  }}");
        if (!Directory.Exists(gamePath))
            return (null, $"Error: game directory not found: {gamePath}");

        return (gamePath, null);
    }

    /// <summary>
    /// Resolves the directory containing <c>Jiangyu.Sdk.dll</c>. Order: explicit
    /// config (<c>sdk</c>, either the directory or the dll path), then the <c>sdk/</c>
    /// folder beside the running executable (published layout), then the dev source
    /// tree (<c>src/Jiangyu.Sdk/bin/{Release,Debug}/net6.0</c>).
    /// </summary>
    public static (string? sdkDir, string? error) ResolveSdkDir(GlobalConfig config)
    {
        if (!string.IsNullOrEmpty(config.Sdk))
        {
            var configured = ExpandHome(config.Sdk);
            if (Directory.Exists(configured) && File.Exists(Path.Combine(configured, "Jiangyu.Sdk.dll")))
                return (configured, null);
            if (File.Exists(configured) && Path.GetFileName(configured) == "Jiangyu.Sdk.dll")
                return (Path.GetDirectoryName(configured), null);
            return (null, $"configured sdk path has no Jiangyu.Sdk.dll: {configured}");
        }

        // The published layout ships the SDK in an sdk/ folder beside the executable
        // (jiangyu / jiangyu-studio). Anchor on the real executable directory via
        // Environment.ProcessPath: a single-file self-extracting publish unpacks to a
        // temp dir, so AppContext.BaseDirectory points there, not at the install folder.
        var bundled = FindSdkDirInRoots(new[] { Path.GetDirectoryName(Environment.ProcessPath), AppContext.BaseDirectory });
        if (bundled is not null)
            return (bundled, null);

        var devSdk = FindDevSdkDir(AppContext.BaseDirectory);
        if (devSdk is not null)
            return (devSdk, null);

        return (null, $"could not locate Jiangyu.Sdk.dll. Set \"sdk\" in {ConfigPath}, or build src/Jiangyu.Sdk.");
    }

    /// <summary>
    /// Locates the bundled SDK in the published layout: an <c>sdk/</c> subfolder beside
    /// one of the candidate <paramref name="roots"/> (preferred), else the root itself.
    /// Pure file probing in declared order, so the first root with the SDK wins and the
    /// resolution is testable without a real published executable. Null when none has it.
    /// </summary>
    internal static string? FindSdkDirInRoots(IEnumerable<string?> roots)
    {
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root))
                continue;
            var sdkSub = Path.Combine(root, "sdk");
            if (File.Exists(Path.Combine(sdkSub, "Jiangyu.Sdk.dll")))
                return sdkSub;
            if (File.Exists(Path.Combine(root, "Jiangyu.Sdk.dll")))
                return root;
        }
        return null;
    }

    /// <summary>
    /// Walks up from a starting directory looking for the dev SDK build output
    /// under <c>src/Jiangyu.Sdk/bin/{Release,Debug}/net6.0/Jiangyu.Sdk.dll</c>.
    /// </summary>
    private static string? FindDevSdkDir(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            foreach (var configuration in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(dir.FullName, "src", "Jiangyu.Sdk", "bin", configuration, "net6.0");
                if (File.Exists(Path.Combine(candidate, "Jiangyu.Sdk.dll")))
                    return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Resolves the Unity editor binary path. Uses explicit config if set,
    /// otherwise scans well-known Unity Hub install locations.
    /// </summary>
    /// <param name="preferredVersion">
    /// If set, prefer an editor whose directory name starts with this version string
    /// (e.g. "6000.0.63f1"). Typically the game's detected Unity version.
    /// </param>
    public static (string? editorPath, string? error) ResolveUnityEditorPath(GlobalConfig config, string? preferredVersion = null)
    {
        if (!string.IsNullOrEmpty(config.UnityEditor))
        {
            var explicit_ = ExpandHome(config.UnityEditor);
            if (File.Exists(explicit_))
                return (explicit_, null);
            return (null, $"Unity editor not found at configured path: {explicit_}");
        }

        var discovered = DiscoverUnityEditor(preferredVersion);
        if (discovered is not null)
            return (discovered, null);

        return (null, $"Unity editor not found. Set it in {ConfigPath}\n\nExample:\n  {{\n    \"unityEditor\": \"/opt/Unity/Hub/Editor/6000.0.63f1/Editor/Unity\"\n  }}");
    }

    /// <summary>
    /// Scans well-known Steam install locations for the MENACE game directory.
    /// </summary>
    internal static string? DiscoverGamePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidates = OperatingSystem.IsWindows()
            ? new[]
            {
                @"C:\Program Files (x86)\Steam\steamapps\common\Menace",
                @"C:\Program Files\Steam\steamapps\common\Menace",
            }
            :
            [
                Path.Combine(home, ".local/share/Steam/steamapps/common/Menace"),
            ];

        return candidates.FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    /// Scans well-known Unity Hub install locations for a Unity editor binary.
    /// If <paramref name="preferredVersion"/> is set, returns that version if found.
    /// Otherwise returns any installed editor as a fallback.
    /// </summary>
    internal static string? DiscoverUnityEditor(string? preferredVersion = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var hubRoots = OperatingSystem.IsWindows()
            ? new[]
            {
                @"C:\Program Files\Unity\Hub\Editor",
            }
            :
            [
                "/opt/Unity/Hub/Editor",
                Path.Combine(home, "Unity/Hub/Editor"),
            ];

        var binaryName = OperatingSystem.IsWindows() ? "Unity.exe" : "Unity";

        string? fallback = null;
        foreach (var hubRoot in hubRoots)
        {
            if (!Directory.Exists(hubRoot))
                continue;

            foreach (var versionDir in Directory.EnumerateDirectories(hubRoot))
            {
                var editorBinary = Path.Combine(versionDir, "Editor", binaryName);
                if (!File.Exists(editorBinary))
                    continue;

                if (preferredVersion is not null &&
                    Path.GetFileName(versionDir).StartsWith(preferredVersion, StringComparison.Ordinal))
                    return editorBinary;

                fallback ??= editorBinary;
            }
        }

        return fallback;
    }

    public static string ExpandHome(string path)
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
