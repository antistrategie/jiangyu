using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniFrame;
using Jiangyu.Core.Config;

namespace Jiangyu.Studio.Host;

/// <summary>
/// Minimal JSON-RPC dispatcher. Receives messages from the frontend,
/// routes to handlers, and sends responses back via the provided callback.
/// </summary>
public static partial class RpcDispatcher
{
    private static readonly Dictionary<string, Func<IInfiniFrameWindow, JsonElement?, JsonElement>> Handlers = new(StringComparer.Ordinal);

    private static readonly JsonElement NullElement = JsonSerializer.SerializeToElement<object?>(null);

    static RpcDispatcher()
    {
        Register("openFolder", HandleOpenFolder);
        Register("openProject", HandleOpenProject);
        Register("listDirectory", HandleListDirectory);
        Register("readFile", HandleReadFile);
        Register("writeFile", HandleWriteFile);
        Register("getConfigStatus", HandleGetConfigStatus);
        Register("setGamePath", HandleSetGamePath);
        Register("setUnityEditorPath", HandleSetUnityEditorPath);
        Register("getRecentProjects", HandleGetRecentProjects);
    }

    public static void Register(string method, Func<IInfiniFrameWindow, JsonElement?, JsonElement> handler)
    {
        Handlers[method] = handler;
    }

    /// <summary>
    /// Push a notification from the host to the frontend. The frontend's
    /// rpc.ts dispatches by `method` when no `id` is present.
    /// </summary>
    public static void SendNotification(IInfiniFrameWindow window, string method, object? parameters)
    {
        var notification = new RpcNotification
        {
            Method = method,
            Params = parameters is null ? null : JsonSerializer.SerializeToElement(parameters),
        };
        window.SendWebMessage(JsonSerializer.Serialize(notification, RpcJsonContext.Default.RpcNotification));
    }

    public static void HandleMessage(IInfiniFrameWindow window, string message, Action<string> sendResponse)
    {
        RpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<RpcRequest>(message);
        }
        catch
        {
            return;
        }

        if (request is null || string.IsNullOrEmpty(request.Method))
            return;

        JsonElement result;
        string? error = null;

        if (Handlers.TryGetValue(request.Method, out var handler))
        {
            try
            {
                result = handler(window, request.Params);
            }
            catch (Exception ex)
            {
                result = NullElement;
                error = ex.Message;
            }
        }
        else
        {
            result = NullElement;
            error = $"Unknown method: {request.Method}";
        }

        var response = new RpcResponse
        {
            Id = request.Id,
            Result = error is null ? result : NullElement,
            Error = error,
        };

        sendResponse(JsonSerializer.Serialize(response, RpcJsonContext.Default.RpcResponse));
    }

    private static JsonElement HandleOpenFolder(IInfiniFrameWindow window, JsonElement? _)
    {
        var results = window.ShowOpenFolder("Open Jiangyu project");
        var path = results.FirstOrDefault(p => p is not null);
        if (path is null)
            return JsonSerializer.SerializeToElement<string?>(null);

        OpenProject(window, path);
        return JsonSerializer.SerializeToElement(path);
    }

    private static JsonElement HandleOpenProject(IInfiniFrameWindow window, JsonElement? parameters)
    {
        var path = parameters?.GetProperty("path").GetString()
                   ?? throw new ArgumentException("Missing 'path' parameter");
        if (!Directory.Exists(path))
            throw new ArgumentException($"Directory not found: {path}");

        OpenProject(window, path);
        return JsonSerializer.SerializeToElement(path);
    }

    private static void OpenProject(IInfiniFrameWindow window, string path)
    {
        var config = GlobalConfig.Load();
        config.RecordRecentProject(path);
        config.Save();

        ProjectWatcher.Start(window, path);
    }

    private static JsonElement HandleListDirectory(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var path = parameters?.GetProperty("path").GetString()
                   ?? throw new ArgumentException("Missing 'path' parameter");

        if (!Directory.Exists(path))
            throw new ArgumentException($"Directory not found: {path}");

        var entries = new List<FileEntry>();

        foreach (var dir in Directory.GetDirectories(path).Order())
        {
            var name = Path.GetFileName(dir);
            if (name == ".git")
                continue;
            entries.Add(new FileEntry { Name = name, Path = dir, IsDirectory = true });
        }

        foreach (var file in Directory.GetFiles(path).Order())
        {
            entries.Add(new FileEntry { Name = Path.GetFileName(file), Path = file, IsDirectory = false });
        }

        var ignored = GetIgnoredSet(path, entries);
        foreach (var entry in entries)
        {
            if (ignored.Contains(entry.Path))
                entry.IsIgnored = true;
        }

        return JsonSerializer.SerializeToElement(entries);
    }

    private static JsonElement HandleReadFile(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var path = parameters?.GetProperty("path").GetString()
                   ?? throw new ArgumentException("Missing 'path' parameter");

        if (!File.Exists(path))
            throw new ArgumentException($"File not found: {path}");

        var content = File.ReadAllText(path);
        return JsonSerializer.SerializeToElement(content);
    }

    private static JsonElement HandleWriteFile(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var path = parameters?.GetProperty("path").GetString()
                   ?? throw new ArgumentException("Missing 'path' parameter");
        var content = parameters?.GetProperty("content").GetString()
                      ?? throw new ArgumentException("Missing 'content' parameter");

        ProjectWatcher.SuppressFor(path);
        File.WriteAllText(path, content);
        return NullElement;
    }

    /// <summary>
    /// Tries git check-ignore first (handles global ignores, nested .gitignore, etc.).
    /// Falls back to a basic in-process .gitignore parser if git is unavailable.
    /// </summary>
    private static HashSet<string> GetIgnoredSet(string directory, List<FileEntry> entries)
    {
        if (entries.Count == 0)
            return [];

        var result = TryGitCheckIgnore(directory, entries);
        if (result is not null)
            return result;

        return FallbackGitignoreParse(directory, entries);
    }

    private static HashSet<string>? TryGitCheckIgnore(string directory, List<FileEntry> entries)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "check-ignore --stdin")
            {
                WorkingDirectory = directory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
                return null;

            // Read stdout on a separate thread to avoid deadlock
            var result = new HashSet<string>(StringComparer.Ordinal);
            var readTask = Task.Run(() =>
            {
                while (proc.StandardOutput.ReadLine() is { } line)
                {
                    if (!string.IsNullOrEmpty(line))
                        result.Add(line);
                }
            });

            foreach (var entry in entries)
                proc.StandardInput.WriteLine(entry.Path);
            proc.StandardInput.Close();

            readTask.Wait(3000);
            proc.WaitForExit(3000);
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static HashSet<string> FallbackGitignoreParse(string directory, List<FileEntry> entries)
    {
        var patterns = new List<string>();

        // Walk up to collect .gitignore files
        var current = directory;
        while (current is not null)
        {
            var gitignorePath = System.IO.Path.Combine(current, ".gitignore");
            if (File.Exists(gitignorePath))
            {
                foreach (var line in File.ReadAllLines(gitignorePath))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                        continue;
                    patterns.Add(trimmed);
                }
            }

            if (Directory.Exists(System.IO.Path.Combine(current, ".git")))
                break;
            current = System.IO.Path.GetDirectoryName(current);
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (MatchesIgnorePattern(entry, patterns))
                result.Add(entry.Path);
        }

        return result;
    }

    private static bool MatchesIgnorePattern(FileEntry entry, List<string> patterns)
    {
        var name = entry.Name;
        foreach (var pattern in patterns)
        {
            if (pattern.StartsWith('!'))
                continue;

            var p = pattern.TrimEnd('/');

            // Direct name match (e.g. "bin", "obj", "node_modules")
            if (p == name)
                return true;

            // Simple wildcard (e.g. "*.user", "*.dll")
            if (p.StartsWith('*') && name.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase))
                return true;

            // Directory-only pattern (e.g. "bin/") — only match directories
            if (pattern.EndsWith('/') && entry.IsDirectory && p == name)
                return true;
        }

        return false;
    }

    internal sealed class FileEntry
    {
        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("path")]
        public required string Path { get; set; }

        [JsonPropertyName("isDirectory")]
        public required bool IsDirectory { get; set; }

        [JsonPropertyName("isIgnored")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsIgnored { get; set; }
    }

    private static JsonElement HandleGetConfigStatus(IInfiniFrameWindow _, JsonElement? __)
    {
        var config = GlobalConfig.Load();
        var (gamePath, gameError) = GlobalConfig.ResolveGameDataPath(config);
        var (editorPath, editorError) = GlobalConfig.ResolveUnityEditorPath(config);

        string? melonLoaderError = null;
        if (gamePath is not null && !string.IsNullOrEmpty(config.Game))
        {
            var gameDir = GlobalConfig.ExpandHome(config.Game);
            var melonDir = Path.Combine(gameDir, "MelonLoader");
            if (!Directory.Exists(melonDir))
                melonLoaderError = "MelonLoader not installed";
        }

        var status = new ConfigStatus
        {
            GamePath = gamePath is not null && !string.IsNullOrEmpty(config.Game)
                ? GlobalConfig.ExpandHome(config.Game)
                : gamePath,
            GameError = gameError,
            UnityEditorPath = editorPath,
            UnityEditorError = editorError,
            MelonLoaderError = melonLoaderError,
        };

        return JsonSerializer.SerializeToElement(status);
    }

    private static JsonElement HandleSetGamePath(IInfiniFrameWindow window, JsonElement? _)
    {
        var config = GlobalConfig.Load();
        var defaultPath = !string.IsNullOrEmpty(config.Game) ? GlobalConfig.ExpandHome(config.Game) : null;
        var results = window.ShowOpenFolder("Select MENACE game directory", defaultPath: defaultPath);
        var path = results.FirstOrDefault(p => p is not null);
        if (path is null)
            return NullElement;

        config.Game = path;
        config.Save();

        return HandleGetConfigStatus(window, null);
    }

    private static JsonElement HandleSetUnityEditorPath(IInfiniFrameWindow window, JsonElement? _)
    {
        var config = GlobalConfig.Load();
        var defaultDir = !string.IsNullOrEmpty(config.UnityEditor)
            ? Path.GetDirectoryName(GlobalConfig.ExpandHome(config.UnityEditor))
            : null;
        var results = window.ShowOpenFile(
            "Select Unity Editor binary",
            defaultPath: defaultDir,
            filters: [("Unity Editor", ["*"])]);
        var path = results.FirstOrDefault(p => p is not null);
        if (path is null)
            return NullElement;

        config.UnityEditor = path;
        config.Save();

        return HandleGetConfigStatus(window, null);
    }

    private static JsonElement HandleGetRecentProjects(IInfiniFrameWindow _, JsonElement? __)
    {
        var config = GlobalConfig.Load();
        var projects = config.RecentProjects ?? [];
        return JsonSerializer.SerializeToElement(projects);
    }

    internal sealed class ConfigStatus
    {
        [JsonPropertyName("gamePath")]
        public string? GamePath { get; set; }

        [JsonPropertyName("gameError")]
        public string? GameError { get; set; }

        [JsonPropertyName("unityEditorPath")]
        public string? UnityEditorPath { get; set; }

        [JsonPropertyName("unityEditorError")]
        public string? UnityEditorError { get; set; }

        [JsonPropertyName("melonLoaderError")]
        public string? MelonLoaderError { get; set; }
    }

    internal sealed class RpcRequest
    {
        [JsonPropertyName("method")]
        public string? Method { get; set; }

        [JsonPropertyName("params")]
        public JsonElement? Params { get; set; }

        [JsonPropertyName("id")]
        public int Id { get; set; }
    }

    internal sealed class RpcResponse
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("result")]
        public JsonElement? Result { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    internal sealed class RpcNotification
    {
        [JsonPropertyName("method")]
        public required string Method { get; set; }

        [JsonPropertyName("params")]
        public JsonElement? Params { get; set; }
    }

    [JsonSerializable(typeof(RpcResponse))]
    [JsonSerializable(typeof(RpcNotification))]
    internal sealed partial class RpcJsonContext : JsonSerializerContext;
}
