using System.Text.Json;
using System.Text.Json.Serialization;
using AssetRipper.Primitives;
using InfiniFrame;
using Jiangyu.Core.Config;
using Jiangyu.Core.Unity;

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
        Register("newProject", HandleNewProject);
        Register("listDirectory", HandleListDirectory);
        Register("listAllFiles", HandleListAllFiles);
        Register("readFile", HandleReadFile);
        Register("writeFile", HandleWriteFile);
        Register("movePath", HandleMovePath);
        Register("copyPath", HandleCopyPath);
        Register("deletePath", HandleDeletePath);
        Register("createFile", HandleCreateFile);
        Register("createDirectory", HandleCreateDirectory);
        Register("revealInExplorer", HandleRevealInExplorer);
        Register("openExternal", HandleOpenExternal);
        Register("getConfigStatus", HandleGetConfigStatus);
        Register("setGamePath", HandleSetGamePath);
        Register("setUnityEditorPath", HandleSetUnityEditorPath);
        Register("assetsIndexStatus", HandleAssetsIndexStatus);
        Register("assetsIndex", HandleAssetsIndex);
        Register("assetsSearch", HandleAssetsSearch);
        Register("assetsExport", HandleAssetsExport);
        Register("assetsPreview", HandleAssetsPreview);
        Register("pickDirectory", HandlePickDirectory);
        Register("getProjectConfig", HandleGetProjectConfig);
        Register("setProjectAssetExportPath", HandleSetProjectAssetExportPath);
        Register("compile", HandleCompile);
        Register("getCompileSummary", HandleGetCompileSummary);
        Register("templatesIndexStatus", HandleTemplatesIndexStatus);
        Register("templatesIndex", HandleTemplatesIndex);
        Register("templatesSearch", HandleTemplatesSearch);
        Register("templatesQuery", HandleTemplatesQuery);
        Register("openPaneWindow", HandleOpenPaneWindow);
        Register("closeAllPaneWindows", HandleCloseAllPaneWindows);
        Register("updatePaneWindowTabs", HandleUpdatePaneWindowTabs);
        Register("updatePaneWindowBrowserState", HandleUpdatePaneWindowBrowserState);
        Register("setWindowTitle", HandleSetWindowTitle);
        Register("closeSelf", HandleCloseSelf);
        Register("beginTabMove", HandleBeginTabMove);
        Register("completeTabMove", HandleCompleteTabMove);
        Register("beginPaneMove", HandleBeginPaneMove);
        Register("completePaneMove", HandleCompletePaneMove);
    }

    private static void Register(string method, Func<IInfiniFrameWindow, JsonElement?, JsonElement> handler)
    {
        Handlers[method] = handler;
    }

    private static string RequireString(JsonElement? parameters, string name)
    {
        if (parameters is not { } p || !p.TryGetProperty(name, out var prop) || prop.GetString() is not { } value)
            throw new ArgumentException($"Missing '{name}' parameter");
        return value;
    }

    private static long RequireLong(JsonElement? parameters, string name)
    {
        if (parameters is not { } p || !p.TryGetProperty(name, out var prop) || !prop.TryGetInt64(out var value))
            throw new ArgumentException($"Missing '{name}' parameter");
        return value;
    }

    private static string? TryGetString(JsonElement? parameters, string name)
    {
        if (parameters is not { } p || !p.TryGetProperty(name, out var prop))
            return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static int? TryGetInt(JsonElement? parameters, string name)
    {
        if (parameters is not { } p || !p.TryGetProperty(name, out var prop))
            return null;
        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v) ? v : null;
    }

    // Strict: returns false when the two paths are equal. Callers use this to reject
    // moving/copying a directory into itself; the UI-side `isDescendant` includes equality.
    private static bool IsStrictDescendantPath(string ancestor, string descendant)
        => descendant.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    // Defence-in-depth: filesystem ops must target paths inside the currently open
    // project. Today the frontend is trusted and would never send paths outside,
    // but a malicious mod rendering into a shared surface or a bug in the editor
    // could. Silent safety-net — we don't trust the client's sandboxing.
    private static void EnsurePathInsideProject(string path)
    {
        var root = ProjectWatcher.ProjectRoot;
        if (root is null)
            return; // No project open; RPC handlers will fail naturally.

        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var full = Path.GetFullPath(path);
        if (full.Equals(root, cmp)) return;
        if (full.StartsWith(root + Path.DirectorySeparatorChar, cmp)) return;

        throw new UnauthorizedAccessException($"Path is outside the open project: {path}");
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
        // Two-stage parse: (a) pull `id` best-effort so we can still respond to a
        // malformed request with its id, otherwise the frontend's promise hangs;
        // (b) parse the full request. Silent returns would leave promises dangling.
        int? requestId = TryExtractId(message);

        RpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<RpcRequest>(message);
        }
        catch (Exception ex)
        {
            RespondWithError(sendResponse, requestId ?? 0, $"Malformed RPC request: {ex.Message}");
            return;
        }

        if (request is null || string.IsNullOrEmpty(request.Method))
        {
            RespondWithError(sendResponse, requestId ?? request?.Id ?? 0, "Missing 'method' field");
            return;
        }

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

    private static int? TryExtractId(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("id", out var idProp) &&
                idProp.ValueKind == JsonValueKind.Number &&
                idProp.TryGetInt32(out var id))
            {
                return id;
            }
        }
        catch
        {
            // Fall through — best-effort only.
        }
        return null;
    }

    private static void RespondWithError(Action<string> sendResponse, int id, string error)
    {
        var response = new RpcResponse { Id = id, Result = NullElement, Error = error };
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
        var path = RequireString(parameters, "path");
        if (!Directory.Exists(path))
            throw new ArgumentException($"Directory not found: {path}");

        OpenProject(window, path);
        return JsonSerializer.SerializeToElement(path);
    }

    private static void OpenProject(IInfiniFrameWindow window, string path)
    {
        ProjectWatcher.Start(window, path);
    }

    private static JsonElement HandleNewProject(IInfiniFrameWindow window, JsonElement? parameters)
    {
        var name = RequireString(parameters, "name");

        if (Path.IsPathRooted(name) ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar) ||
            name.Contains(".."))
            throw new ArgumentException($"Invalid project name: {name}");

        var results = window.ShowOpenFolder("Choose location for new project");
        var parentDir = results.FirstOrDefault(p => p is not null);
        if (parentDir is null)
            return JsonSerializer.SerializeToElement<string?>(null);

        var projectDir = Path.Combine(parentDir, name);
        Directory.CreateDirectory(projectDir);

        ProjectScaffold.InitAsync(projectDir).GetAwaiter().GetResult();

        OpenProject(window, projectDir);
        return JsonSerializer.SerializeToElement(projectDir);
    }

    private static JsonElement HandleListDirectory(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var path = RequireString(parameters, "path");
        EnsurePathInsideProject(path);

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
        var path = RequireString(parameters, "path");
        EnsurePathInsideProject(path);

        if (!File.Exists(path))
            throw new ArgumentException($"File not found: {path}");

        var content = File.ReadAllText(path);
        return JsonSerializer.SerializeToElement(content);
    }

    // Directories to skip when git isn't available. OrdinalIgnoreCase so Windows
    // paths like `Node_Modules` or `.Git` are still recognised.
    private static readonly HashSet<string> FileSearchSkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "obj", "bin", "dist", "build", "target",
        ".idea", ".vs", ".vscode", ".gradle", ".next", "__pycache__",
        ".venv", "venv", ".cache", ".jiangyu", ".unity"
    };

    private const int FileSearchMaxResults = 10_000;

    private static JsonElement HandleListAllFiles(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var root = RequireString(parameters, "path");
        EnsurePathInsideProject(root);

        if (!Directory.Exists(root))
            throw new ArgumentException($"Directory not found: {root}");

        // Prefer git for gitignore-aware listing so the palette matches the sidebar's
        // hidden/ignored set. Falls back to a manual walk for non-git directories.
        var results = TryGitListFiles(root) ?? WalkDirectoryForFiles(root);
        return JsonSerializer.SerializeToElement(results);
    }

    private static List<string>? TryGitListFiles(string root)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "ls-files -co --exclude-standard")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return null;

            var results = new List<string>(capacity: 1024);
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                if (results.Count >= FileSearchMaxResults) break;
                results.Add(line);
            }
            process.WaitForExit();
            return process.ExitCode == 0 ? results : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> WalkDirectoryForFiles(string root)
    {
        var results = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0 && results.Count < FileSearchMaxResults)
        {
            var dir = stack.Pop();

            IEnumerable<string> files;
            IEnumerable<string> subdirs;
            try
            {
                files = Directory.EnumerateFiles(dir);
                subdirs = Directory.EnumerateDirectories(dir);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var file in files)
            {
                if (results.Count >= FileSearchMaxResults) break;
                results.Add(Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'));
            }

            foreach (var sub in subdirs)
            {
                if (FileSearchSkipDirs.Contains(Path.GetFileName(sub)))
                    continue;
                stack.Push(sub);
            }
        }

        return results;
    }

    // 64 MiB — generous enough for any plausible source file, small enough that
    // a pathological paste-into-Monaco-and-save doesn't OOM the host.
    private const int MaxWriteFileBytes = 64 * 1024 * 1024;

    private static JsonElement HandleWriteFile(IInfiniFrameWindow window, JsonElement? parameters)
    {
        var path = RequireString(parameters, "path");
        var content = RequireString(parameters, "content");
        EnsurePathInsideProject(path);

        // UTF-8 upper bound: 4 bytes per char. Fast reject before we allocate.
        if (content.Length > MaxWriteFileBytes)
            throw new IOException($"File content exceeds {MaxWriteFileBytes / (1024 * 1024)} MiB limit");

        // Atomic write: stage in a sibling tmp file, then rename over the target.
        // File.WriteAllText truncates-then-writes in place; a mid-write crash
        // leaves a corrupt file, which for an editor is real data loss.
        //
        // Suppression is scoped to this window's id: other open windows still
        // receive the fileChanged notification and surface the conflict banner,
        // mirroring how an external editor overwriting a file looks to them.
        var tmp = path + ".jiangyu.tmp";
        ProjectWatcher.SuppressFor(path, window.Id);
        ProjectWatcher.SuppressFor(tmp, window.Id);
        try
        {
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore cleanup failure */ }
            throw;
        }
        return NullElement;
    }

    private static JsonElement HandleMovePath(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var src = RequireString(parameters, "srcPath");
        var dest = RequireString(parameters, "destPath");
        EnsurePathInsideProject(src);
        EnsurePathInsideProject(dest);

        if (Directory.Exists(src))
        {
            if (IsStrictDescendantPath(src, dest))
                throw new IOException("Cannot move a directory into itself");
            Directory.Move(src, dest);
        }
        else if (File.Exists(src))
        {
            File.Move(src, dest);
        }
        else
        {
            throw new FileNotFoundException($"Source not found: {src}");
        }

        return NullElement;
    }

    private static JsonElement HandleCopyPath(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var src = RequireString(parameters, "srcPath");
        var dest = RequireString(parameters, "destPath");
        EnsurePathInsideProject(src);
        EnsurePathInsideProject(dest);

        // Directory.CreateDirectory (used by recursive copy) is idempotent and
        // File.Copy's no-overwrite default doesn't cover dest being a directory.
        if (File.Exists(dest) || Directory.Exists(dest))
            throw new IOException($"Destination already exists: {dest}");

        if (Directory.Exists(src))
        {
            if (IsStrictDescendantPath(src, dest))
                throw new IOException("Cannot copy a directory into itself");
            CopyDirectoryRecursive(src, dest);
        }
        else if (File.Exists(src))
        {
            File.Copy(src, dest);
        }
        else
        {
            throw new FileNotFoundException($"Source not found: {src}");
        }

        return NullElement;
    }

    private static void CopyDirectoryRecursive(string src, string dest)
    {
        // Sequential: project copies are small enough that thread-pool overhead
        // outweighs any parallelism win, and concurrent writes are a regression
        // on HDDs. Sharing a queue across sibling directories would matter only
        // at much larger scales than this tool handles.
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(src))
            CopyDirectoryRecursive(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    private static JsonElement HandleDeletePath(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var path = RequireString(parameters, "path");
        EnsurePathInsideProject(path);

        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else if (File.Exists(path))
            File.Delete(path);
        else
            throw new FileNotFoundException($"Not found: {path}");

        return NullElement;
    }

    private static JsonElement HandleCreateFile(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var path = RequireString(parameters, "path");
        EnsurePathInsideProject(path);

        // File.Create silently truncates; guard to treat existing paths as an error.
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException($"Already exists: {path}");

        using (File.Create(path)) { }
        return NullElement;
    }

    private static JsonElement HandleCreateDirectory(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var path = RequireString(parameters, "path");
        EnsurePathInsideProject(path);

        // Directory.CreateDirectory is idempotent; guard to treat existing paths as an error.
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException($"Already exists: {path}");

        Directory.CreateDirectory(path);
        return NullElement;
    }

    private static JsonElement HandleOpenExternal(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var url = RequireString(parameters, "url");

        // Only allow http / https. The UI today only calls this with our own
        // repo URL, but the guard prevents the RPC from being weaponised into
        // launching arbitrary `file://`, `javascript:` or custom-scheme handlers
        // if a future caller ever forwards a user-controlled string.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Invalid external URL: {url}");
        }

        if (OperatingSystem.IsWindows())
        {
            // UseShellExecute routes the URL through the default browser.
            var psi = new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        else if (OperatingSystem.IsMacOS())
        {
            System.Diagnostics.Process.Start("open", [uri.AbsoluteUri]);
        }
        else
        {
            System.Diagnostics.Process.Start("xdg-open", [uri.AbsoluteUri]);
        }

        return NullElement;
    }

    private static JsonElement HandleRevealInExplorer(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var path = RequireString(parameters, "path");

        if (OperatingSystem.IsWindows())
        {
            // ArgumentList avoids quoting bugs and command-injection via paths containing `"`.
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe");
            psi.ArgumentList.Add($"/select,{path}");
            System.Diagnostics.Process.Start(psi);
        }
        else if (OperatingSystem.IsMacOS())
        {
            System.Diagnostics.Process.Start("open", ["-R", path]);
        }
        else
        {
            // xdg-open can't select a specific file — open the containing directory instead.
            var target = File.Exists(path) ? Path.GetDirectoryName(path) ?? path : path;
            System.Diagnostics.Process.Start("xdg-open", [target]);
        }

        return NullElement;
    }

    /// <summary>
    /// Uses `git check-ignore` to identify files excluded by .gitignore /
    /// .git/info/exclude / global excludes. When git isn't available or the
    /// directory isn't a repo, returns an empty set — a home-grown matcher
    /// was tried before and was weak enough to mislead users; "nothing ignored"
    /// is more honest than "some subset that looks right".
    /// </summary>
    private static HashSet<string> GetIgnoredSet(string directory, List<FileEntry> entries)
    {
        if (entries.Count == 0)
            return [];

        return TryGitCheckIgnore(directory, entries) ?? [];
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

    // Cache the game Unity version per resolved game-data path. DetectGameVersion
    // loads the AssetRipper GameStructure which is measurable-seconds slow; the
    // welcome screen + settings modal both poll getConfigStatus on open, so
    // without a cache every open would stall the UI.
    private static string? _cachedUnityVersionGamePath;
    private static UnityVersion? _cachedUnityGameVersion;

    private static UnityVersion? GetGameUnityVersionCached(string gameDataPath)
    {
        if (string.Equals(_cachedUnityVersionGamePath, gameDataPath, StringComparison.Ordinal) && _cachedUnityGameVersion is not null)
            return _cachedUnityGameVersion;
        try
        {
            var version = UnityVersionValidationService.DetectGameVersion(gameDataPath);
            _cachedUnityVersionGamePath = gameDataPath;
            _cachedUnityGameVersion = version;
            return version;
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement HandleGetConfigStatus(IInfiniFrameWindow _, JsonElement? __)
    {
        var config = GlobalConfig.Load();
        var (gamePath, gameError) = GlobalConfig.ResolveGameDataPath(config);

        // Detect the game's Unity version so we can (a) steer editor discovery
        // toward the matching install and (b) flag mismatches at config time
        // instead of waiting for the user to hit compile.
        UnityVersion? gameVersion = gamePath is not null ? GetGameUnityVersionCached(gamePath) : null;

        var (editorPath, editorError) = GlobalConfig.ResolveUnityEditorPath(config, gameVersion?.ToString());

        // If discovery or explicit config produced an editor path, compare its
        // version to the game's. Editor version is parsed from the path (cheap,
        // no process spawn) — Unity Hub installs always embed the version in
        // the directory name. Mismatch is surfaced via unityEditorError so the
        // welcome screen and settings modal can render a warning.
        UnityVersion? editorVersion = null;
        if (editorPath is not null &&
            UnityVersionValidationService.TryParseUnityVersionFromText(editorPath, out var parsed))
        {
            editorVersion = parsed;
        }

        if (editorPath is not null && gameVersion is not null && editorVersion is not null && editorVersion != gameVersion)
        {
            editorError = $"Unity {gameVersion} required (editor is {editorVersion}).";
        }

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
            GameUnityVersion = gameVersion?.ToString(),
            UnityEditorPath = editorPath,
            UnityEditorError = editorError,
            UnityEditorVersion = editorVersion?.ToString(),
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

    internal sealed class ConfigStatus
    {
        [JsonPropertyName("gamePath")]
        public string? GamePath { get; set; }

        [JsonPropertyName("gameError")]
        public string? GameError { get; set; }

        [JsonPropertyName("gameUnityVersion")]
        public string? GameUnityVersion { get; set; }

        [JsonPropertyName("unityEditorPath")]
        public string? UnityEditorPath { get; set; }

        [JsonPropertyName("unityEditorError")]
        public string? UnityEditorError { get; set; }

        [JsonPropertyName("unityEditorVersion")]
        public string? UnityEditorVersion { get; set; }

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
