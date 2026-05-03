using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniFrame;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;
using Jiangyu.Studio.Rpc;
using static Jiangyu.Studio.Rpc.RpcHelpers;

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
        // Window-bound, Host-only handlers (file dialogs, project lifecycle,
        // window/tab management, settings, agent management). These need
        // IInfiniFrameWindow or other Host-private state and stay here.
        Register("openFolder", HandleOpenFolder);
        Register("openProject", HandleOpenProject);
        Register("newProject", HandleNewProject);
        Register("getGitBranch", HandleGetGitBranch);
        Register("readFile", HandleReadFile);
        Register("writeFile", HandleWriteFile);
        Register("revealInExplorer", HandleRevealInExplorer);
        Register("openExternal", HandleOpenExternal);
        Register("setGamePath", HandleSetGamePath);
        Register("setUnityEditorPath", HandleSetUnityEditorPath);
        Register("pickDirectory", HandlePickDirectory);
        Register("setProjectAssetExportPath", HandleSetProjectAssetExportPath);
        Register("compile", HandleCompile);
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

        // Pure handlers — body lives in Jiangyu.Studio.Rpc/RpcHandlers.*.cs.
        // Both this dispatcher and the standalone Mcp binary call into the
        // same code, with the dispatch lock acquired around each invocation.
        // The MCP server takes its own lock (RpcHandlers.DispatchLock); the
        // WebView dispatcher takes ours below in HandleMessage; the inner
        // body just runs.
        Register("listDirectory", (_, p) => RpcHandlers.ListDirectory(p));
        Register("listAllFiles", (_, p) => RpcHandlers.ListAllFiles(p));
        Register("movePath", (_, p) => RpcHandlers.MovePath(p));
        Register("copyPath", (_, p) => RpcHandlers.CopyPath(p));
        Register("deletePath", (_, p) => RpcHandlers.DeletePath(p));
        Register("createFile", (_, p) => RpcHandlers.CreateFile(p));
        Register("editFile", (window, p) => EditFileWithSuppression(window?.Id, p));
        Register("createDirectory", (_, p) => RpcHandlers.CreateDirectory(p));
        Register("grepFiles", (_, p) => RpcHandlers.GrepFiles(p));
        Register("getConfigStatus", (_, p) => RpcHandlers.GetConfigStatus(p));
        Register("assetsIndexStatus", (_, p) => RpcHandlers.AssetsIndexStatus(p));
        Register("assetsIndex", (_, p) => RpcHandlers.AssetsIndex(p));
        Register("assetsSearch", (_, p) => RpcHandlers.AssetsSearch(p));
        Register("assetsExport", (_, p) => RpcHandlers.AssetsExport(p));
        Register("assetsPreview", (_, p) => RpcHandlers.AssetsPreview(p));
        Register("getProjectConfig", (_, p) => RpcHandlers.GetProjectConfig(p));
        Register("compileBlocking", (_, p) => RpcHandlers.CompileBlocking(p));
        Register("getCompileSummary", (_, p) => RpcHandlers.GetCompileSummary(p));
        Register("templatesIndexStatus", (_, p) => RpcHandlers.TemplatesIndexStatus(p));
        Register("templatesIndex", (_, p) => RpcHandlers.TemplatesIndex(p));
        Register("templatesSearch", (_, p) => RpcHandlers.TemplatesSearch(p));
        Register("templatesQuery", (_, p) => RpcHandlers.TemplatesQuery(p));
        Register("templatesParse", (_, p) => RpcHandlers.TemplatesParse(p));
        Register("templatesSerialise", (_, p) => RpcHandlers.TemplatesSerialise(p));
        Register("templatesEnumMembers", (_, p) => RpcHandlers.TemplatesEnumMembers(p));
        Register("templatesProjectClones", (_, p) => RpcHandlers.TemplatesProjectClones(p));
        Register("templatesInspect", (_, p) => RpcHandlers.TemplatesInspect(p));
        Register("templatesValue", (_, p) => RpcHandlers.TemplatesValue(p));

        RegisterAgentHandlers();
    }

    static partial void RegisterAgentHandlers();

    private static void Register(string method, Func<IInfiniFrameWindow, JsonElement?, JsonElement> handler)
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
                lock (RpcHandlers.DispatchLock) result = handler(window, request.Params);
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

    // readFile and writeFile are NOT exposed via MCP — ACP agents reach
    // them via the standard `fs/read_text_file` and `fs/write_text_file`
    // methods, which route through AcpClientHandler. The body lives in
    // Studio.Rpc so the same code paths run from either entry point;
    // these wrappers just notify ProjectWatcher about per-window suppression
    // of the resulting watcher event so the same window doesn't see its
    // own save as a conflict.
    private static JsonElement HandleReadFile(IInfiniFrameWindow _, JsonElement? parameters)
        => RpcHandlers.ReadFile(parameters);

    private static JsonElement HandleWriteFile(IInfiniFrameWindow window, JsonElement? parameters)
        => WriteFileWithSuppression(window?.Id, parameters);

    /// <summary>
    /// Records a watcher-event suppression for the calling window before
    /// delegating to <see cref="RpcHandlers.WriteFile"/>. Tests reach this
    /// directly with an explicit Guid; the dispatcher closure just unwraps
    /// the window. Null <paramref name="windowId"/> (MCP path, no calling
    /// window) skips suppression — every watcher subscriber sees the event.
    /// </summary>
    internal static JsonElement WriteFileWithSuppression(Guid? windowId, JsonElement? parameters)
    {
        if (windowId is { } id)
        {
            var path = RequireString(parameters, "path");
            ProjectWatcher.SuppressFor(path, id);
            ProjectWatcher.SuppressFor(path + ".jiangyu.tmp", id);
        }
        return RpcHandlers.WriteFile(parameters);
    }

    /// <summary>
    /// Same shape as <see cref="WriteFileWithSuppression"/> but for editFile;
    /// kept distinct so the two pathways can diverge (e.g. if editFile ever
    /// stops staging through a tmp file).
    /// </summary>
    internal static JsonElement EditFileWithSuppression(Guid? windowId, JsonElement? parameters)
    {
        if (windowId is { } id)
        {
            var path = RequireString(parameters, "path");
            ProjectWatcher.SuppressFor(path, id);
            ProjectWatcher.SuppressFor(path + ".jiangyu.tmp", id);
        }
        return RpcHandlers.EditFile(parameters);
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

    private static JsonElement HandleSetGamePath(IInfiniFrameWindow window, JsonElement? _)
    {
        var config = Jiangyu.Core.Config.GlobalConfig.Load();
        var defaultPath = !string.IsNullOrEmpty(config.Game) ? Jiangyu.Core.Config.GlobalConfig.ExpandHome(config.Game) : null;
        var results = window.ShowOpenFolder("Select MENACE game directory", defaultPath: defaultPath);
        var path = results.FirstOrDefault(p => p is not null);
        if (path is null)
            return NullElement;

        config.Game = path;
        config.Save();

        return RpcHandlers.GetConfigStatus(null);
    }

    private static JsonElement HandleSetUnityEditorPath(IInfiniFrameWindow window, JsonElement? _)
    {
        var config = Jiangyu.Core.Config.GlobalConfig.Load();
        var defaultDir = !string.IsNullOrEmpty(config.UnityEditor)
            ? Path.GetDirectoryName(Jiangyu.Core.Config.GlobalConfig.ExpandHome(config.UnityEditor))
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

        return RpcHandlers.GetConfigStatus(null);
    }

    private static JsonElement HandleSetProjectAssetExportPath(IInfiniFrameWindow _, JsonElement? parameters)
        => RpcHandlers.SetProjectAssetExportPath(parameters);

    /// <summary>
    /// Preloads the template index/values caches on a background thread so the
    /// template browser doesn't freeze the app when it first mounts. Forwards
    /// to the moved handler.
    /// </summary>
    public static void PreloadTemplateCaches() => RpcHandlers.PreloadTemplateCaches();

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
