using System.Text.Json;
using System.Text.Json.Serialization;
using AssetRipper.Primitives;
using InfiniFrame;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;
using Jiangyu.Core.Unity;
using Jiangyu.Shared;

namespace Jiangyu.Studio.Host.Rpc;

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
        Register("getGitBranch", HandleGetGitBranch);
        Register("listDirectory", HandleListDirectory);
        Register("listAllFiles", HandleListAllFiles);
        Register("readFile", HandleReadFile);
        Register("writeFile", HandleWriteFile);
        Register("movePath", HandleMovePath);
        Register("copyPath", HandleCopyPath);
        Register("deletePath", HandleDeletePath);
        Register("createFile", HandleCreateFile);
        Register("editFile", HandleEditFile);
        Register("createDirectory", HandleCreateDirectory);
        Register("grepFiles", HandleGrepFiles);
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
        Register("compileBlocking", HandleCompileBlocking);
        Register("getCompileSummary", HandleGetCompileSummary);
        Register("templatesIndexStatus", HandleTemplatesIndexStatus);
        Register("templatesIndex", HandleTemplatesIndex);
        Register("templatesSearch", HandleTemplatesSearch);
        Register("templatesQuery", HandleTemplatesQuery);
        Register("templatesParse", HandleTemplatesParse);
        Register("templatesSerialise", HandleTemplatesSerialise);
        Register("templatesEnumMembers", HandleTemplatesEnumMembers);
        Register("templatesProjectClones", HandleTemplatesProjectClones);
        Register("templatesInspect", HandleTemplatesInspect);
        Register("templatesValue", HandleTemplatesValue);
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
        Register("getStudioSettings", HandleGetStudioSettings);
        Register("setStudioSetting", HandleSetStudioSetting);

        RegisterAgentHandlers();
    }

    static partial void RegisterAgentHandlers();

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

    // The UI's path utilities and Monaco model URIs expect forward slashes on
    // every platform. Native APIs return backslashes on Windows, so normalise
    // at the host boundary before sending paths to the frontend.
    internal static string NormaliseSeparators(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/');

    // Defence-in-depth: filesystem ops must target paths inside the currently open
    // project. Today the frontend is trusted and would never send paths outside,
    // but a malicious mod rendering into a shared surface or a bug in the editor
    // could. Silent safety-net — we don't trust the client's sandboxing.
    //
    // Symlinks are resolved before the prefix check so a link inside the project
    // pointing outside it (e.g. `Mods/external -> /etc`) doesn't escape. Path
    // components above the project root (`..`) are normalised by GetFullPath.
    // For paths that don't exist yet (a write target), we walk up the chain
    // until we find an existing parent and resolve from there.
    internal static void EnsurePathInsideProject(string path)
    {
        var root = ProjectWatcher.ProjectRoot;
        if (root is null)
            throw new InvalidOperationException("No project open");

        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var resolvedRoot = ResolveRealPath(root) ?? Path.GetFullPath(root);
        var resolved = ResolveRealPath(path) ?? Path.GetFullPath(path);

        if (resolved.Equals(resolvedRoot, cmp)) return;
        if (resolved.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, cmp)) return;

        throw new UnauthorizedAccessException($"Path is outside the open project: {path}");
    }

    /// <summary>
    /// Best-effort canonical-path resolution: follows symlinks for the deepest
    /// existing component, then re-attaches the trailing path segments.
    /// Returns null if <paramref name="path"/> can't be normalised at all.
    /// </summary>
    private static string? ResolveRealPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            // Walk up to find an existing component, since ResolveLinkTarget
            // returns null for non-existent paths.
            var existing = full;
            var trailing = "";
            while (!File.Exists(existing) && !Directory.Exists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (string.IsNullOrEmpty(parent) || parent == existing) return full;
                trailing = Path.Combine(Path.GetFileName(existing), trailing);
                existing = parent;
            }

            // ResolveLinkTarget returns null when the path is not a link;
            // in that case the path is already canonical at this level.
            FileSystemInfo info = Directory.Exists(existing)
                ? new DirectoryInfo(existing)
                : new FileInfo(existing);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            var resolvedExisting = target?.FullName ?? existing;
            return string.IsNullOrEmpty(trailing)
                ? resolvedExisting
                : Path.Combine(resolvedExisting, trailing);
        }
        catch
        {
            return null;
        }
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

    /// <summary>
    /// Single-handler-at-a-time gate. The WebView dispatch is implicitly
    /// single-threaded by InfiniFrame, but MCP runs on the ASP.NET thread
    /// pool — without serialisation, MCP and WebView would race on the
    /// shared statics (project root, asset/template indexes, agent manager).
    /// All RPC handler invocations from either path must acquire this lock.
    /// </summary>
    internal static readonly Lock DispatchLock = new();

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
                lock (DispatchLock) result = handler(window, request.Params);
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
        return JsonSerializer.SerializeToElement(NormaliseSeparators(path));
    }

    private static JsonElement HandleOpenProject(IInfiniFrameWindow window, JsonElement? parameters)
    {
        var path = RequireString(parameters, "path");
        if (!Directory.Exists(path))
            throw new ArgumentException($"Directory not found: {path}");

        OpenProject(window, path);
        return JsonSerializer.SerializeToElement(NormaliseSeparators(path));
    }

    private static void OpenProject(IInfiniFrameWindow window, string path)
    {
        if (!File.Exists(Path.Combine(path, ModManifest.FileName)))
            throw new ArgumentException($"Not a Jiangyu project — missing {ModManifest.FileName} in: {path}");

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
        return JsonSerializer.SerializeToElement(NormaliseSeparators(projectDir));
    }

    /// <summary>
    /// Returns the current git branch name for the given path, or null when
    /// git isn't installed, the path isn't inside a git repo, or HEAD is
    /// detached. Catch-everything so a missing git binary or damaged repo
    /// surfaces as "no branch indicator" rather than a visible error.
    /// </summary>
    private static JsonElement HandleGetGitBranch(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var path = RequireString(parameters, "path");
        EnsurePathInsideProject(path);

        return JsonSerializer.SerializeToElement(TryGetGitBranch(path));
    }

    private static string? TryGetGitBranch(string path)
    {
        try
        {
            // `symbolic-ref --short HEAD` returns the branch name on success
            // and exits non-zero on detached HEAD — exactly the "no label"
            // signal we want, rather than the literal string "HEAD" that
            // `rev-parse --abbrev-ref HEAD` would give back.
            var psi = new System.Diagnostics.ProcessStartInfo("git", "symbolic-ref --short HEAD")
            {
                WorkingDirectory = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return null;

            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(3000))
            {
                try { proc.Kill(); } catch { /* ignore */ }
                return null;
            }
            if (proc.ExitCode != 0) return null;

            var trimmed = output.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }
        catch
        {
            return null;
        }
    }

    [McpTool("jiangyu_list_directory",
        "List files and subdirectories in a project directory. Returns array of {name, path, isDirectory, isIgnored, size} entries. size is in bytes (files only). Path must be inside the open project.")]
    [McpParam("path", "string", "Absolute path to the directory to list.", Required = true)]
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
            entries.Add(new FileEntry { Name = name, Path = NormaliseSeparators(dir), IsDirectory = true });
        }

        foreach (var file in Directory.GetFiles(path).Order())
        {
            var info = new FileInfo(file);
            entries.Add(new FileEntry
            {
                Name = Path.GetFileName(file),
                Path = NormaliseSeparators(file),
                IsDirectory = false,
                Size = info.Length,
            });
        }

        var ignored = GetIgnoredSet(path, entries);
        foreach (var entry in entries)
        {
            if (ignored.Contains(entry.Path))
                entry.IsIgnored = true;
        }

        return JsonSerializer.SerializeToElement(entries);
    }

    // Not exposed via MCP: ACP agents use the standard `fs/read_text_file`
    // method instead, which routes through AcpClientHandler.ReadTextFileAsync.
    // Keeping a duplicate MCP tool would let the model pick inconsistently
    // between the two paths.
    private static JsonElement HandleReadFile(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var path = RequireString(parameters, "path");
        EnsurePathInsideProject(path);

        if (!File.Exists(path))
            throw new ArgumentException($"File not found: {path}");

        var startLine = TryGetInt(parameters, "startLine");
        var endLine = TryGetInt(parameters, "endLine");

        if (startLine.HasValue || endLine.HasValue)
        {
            var start = startLine ?? 1;
            var end = endLine ?? int.MaxValue;
            if (start < 1) start = 1;
            if (end < start) throw new ArgumentException("endLine must be >= startLine");

            var lines = File.ReadLines(path)
                .Skip(start - 1)
                .Take(end - start + 1);
            return JsonSerializer.SerializeToElement(string.Join('\n', lines));
        }

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

    [McpTool("jiangyu_list_all_files",
        "List all files in the project tree (gitignore-aware). Returns array of relative file paths. Max 10,000 results. Path must be inside the open project.")]
    [McpParam("path", "string", "Absolute path to the project root directory.", Required = true)]
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

    // Not exposed via MCP: ACP agents use the standard `fs/write_text_file`
    // method instead. See HandleReadFile above for the rationale.
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
        // When called via MCP (window is null) suppression is skipped; all
        // windows see the change notification.
        var tmp = path + ".jiangyu.tmp";
        if (window is not null)
        {
            ProjectWatcher.SuppressFor(path, window.Id);
            ProjectWatcher.SuppressFor(tmp, window.Id);
        }
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

    [McpTool("jiangyu_move_path",
        "Rename or move a file or directory within the project. Both paths must be inside the open project.")]
    [McpParam("srcPath", "string", "Absolute path of the source file or directory.", Required = true)]
    [McpParam("destPath", "string", "Absolute path of the destination.", Required = true)]
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

    [McpTool("jiangyu_copy_path",
        "Copy a file or directory within the project. Fails if destination already exists. Both paths must be inside the open project.")]
    [McpParam("srcPath", "string", "Absolute path of the source file or directory.", Required = true)]
    [McpParam("destPath", "string", "Absolute path of the destination.", Required = true)]
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

    [McpTool("jiangyu_delete_path",
        "Delete a file or directory (recursive) in the project. Path must be inside the open project.")]
    [McpParam("path", "string", "Absolute path to the file or directory to delete.", Required = true)]
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

    [McpTool("jiangyu_create_file",
        "Create an empty file in the project. Fails if the path already exists. Path must be inside the open project.")]
    [McpParam("path", "string", "Absolute path for the new file.", Required = true)]
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

    [McpTool("jiangyu_edit_file",
        "Replace exactly one occurrence of oldText with newText in a file. Fails if oldText is not found or appears more than once. Path must be inside the open project.")]
    [McpParam("path", "string", "Absolute path to the file to edit.", Required = true)]
    [McpParam("oldText", "string", "Exact text to find (must appear exactly once).", Required = true)]
    [McpParam("newText", "string", "Replacement text.", Required = true)]
    private static JsonElement HandleEditFile(IInfiniFrameWindow window, JsonElement? parameters)
    {
        var path = RequireString(parameters, "path");
        var oldText = RequireString(parameters, "oldText");
        var newText = RequireString(parameters, "newText");
        EnsurePathInsideProject(path);

        if (!File.Exists(path))
            throw new ArgumentException($"File not found: {path}");

        var content = File.ReadAllText(path);
        var firstIdx = content.IndexOf(oldText, StringComparison.Ordinal);
        if (firstIdx < 0)
            throw new ArgumentException("oldText not found in file");

        var secondIdx = content.IndexOf(oldText, firstIdx + oldText.Length, StringComparison.Ordinal);
        if (secondIdx >= 0)
            throw new ArgumentException("oldText appears more than once; include more context to make it unique");

        var updated = string.Concat(content.AsSpan(0, firstIdx), newText, content.AsSpan(firstIdx + oldText.Length));

        // Atomic write via sibling tmp file.
        var tmpPath = path + ".jiangyu.tmp";
        try
        {
            File.WriteAllText(tmpPath, updated);
            File.Move(tmpPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }

        if (window is not null)
            ProjectWatcher.SuppressFor(path, window.Id);

        return NullElement;
    }

    [McpTool("jiangyu_create_directory",
        "Create a directory in the project. Fails if the path already exists. Path must be inside the open project.")]
    [McpParam("path", "string", "Absolute path for the new directory.", Required = true)]
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

    private const int GrepMaxResults = 200;
    private const int GrepMaxLineLength = 500;

    [McpTool("jiangyu_grep",
        "Search file contents in the project for a substring. Returns array of {file, line, text} matches. Case-sensitive.")]
    [McpParam("pattern", "string", "Substring to search for in file contents.", Required = true)]
    [McpParam("path", "string", "Absolute path to directory to search. Defaults to project root.")]
    [McpParam("glob", "string", "Filename filter glob pattern (e.g. \"*.kdl\").")]
    [McpParam("limit", "integer", "Maximum number of results to return. Default 200.")]
    private static JsonElement HandleGrepFiles(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var pattern = RequireString(parameters, "pattern");
        var root = TryGetString(parameters, "path") ?? ProjectWatcher.ProjectRoot
            ?? throw new InvalidOperationException("No project open");
        var globFilter = TryGetString(parameters, "glob");
        var limit = TryGetInt(parameters, "limit") ?? GrepMaxResults;

        EnsurePathInsideProject(root);

        if (!Directory.Exists(root))
            throw new ArgumentException($"Directory not found: {root}");

        var results = new List<object>();
        SearchDirectory(root, pattern, globFilter, limit, results);
        return JsonSerializer.SerializeToElement(results);
    }

    private static void SearchDirectory(string dir, string pattern, string? globFilter, int limit, List<object> results)
    {
        if (results.Count >= limit) return;

        var dirName = Path.GetFileName(dir);
        if (FileSearchSkipDirs.Contains(dirName)) return;

        try
        {
            foreach (var file in Directory.GetFiles(dir).Order())
            {
                if (results.Count >= limit) return;

                if (globFilter is not null && !FileMatchesGlob(Path.GetFileName(file), globFilter))
                    continue;

                SearchFile(file, pattern, limit, results);
            }

            foreach (var subDir in Directory.GetDirectories(dir).Order())
            {
                if (results.Count >= limit) return;
                SearchDirectory(subDir, pattern, globFilter, limit, results);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't read.
        }
    }

    private static void SearchFile(string file, string pattern, int limit, List<object> results)
    {
        try
        {
            var lineNum = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNum++;
                if (results.Count >= limit) return;

                if (line.Contains(pattern, StringComparison.Ordinal))
                {
                    var text = line.Length > GrepMaxLineLength
                        ? line[..GrepMaxLineLength] + "…"
                        : line;
                    results.Add(new
                    {
                        file = NormaliseSeparators(file),
                        line = lineNum,
                        text = text.TrimStart(),
                    });
                }
            }
        }
        catch (IOException)
        {
            // Skip files we can't read (binary, locked, etc.).
        }
    }

    private static bool FileMatchesGlob(string fileName, string glob)
    {
        // Simple glob: *.ext or *pattern* — covers the common cases.
        if (glob.StartsWith('*') && glob.LastIndexOf('*') == 0)
        {
            // *.kdl — suffix match
            return fileName.EndsWith(glob[1..], StringComparison.OrdinalIgnoreCase);
        }
        if (glob.StartsWith('*') && glob.EndsWith('*'))
        {
            // *pattern* — contains match
            return fileName.Contains(glob[1..^1], StringComparison.OrdinalIgnoreCase);
        }

        return fileName.Equals(glob, StringComparison.OrdinalIgnoreCase);
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

    [RpcType]
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

        [JsonPropertyName("size")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long Size { get; set; }
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

    [McpTool("jiangyu_config_status",
        "Get Studio configuration status: game path, game Unity version, Unity editor path, MelonLoader health. Returns {gamePath?, gameError?, gameUnityVersion?, unityEditorPath?, unityEditorError?, unityEditorVersion?, melonLoaderError?}.")]
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

    [RpcType]
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
