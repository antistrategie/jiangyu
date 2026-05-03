using System.Text.Json;
using System.Text.Json.Serialization;
using AssetRipper.Primitives;
using Jiangyu.Core.Config;
using Jiangyu.Core.Unity;
using Jiangyu.Shared;
using static Jiangyu.Studio.Rpc.RpcHelpers;

namespace Jiangyu.Studio.Rpc;

public static partial class RpcHandlers
{
    // Directories to skip when git isn't available. OrdinalIgnoreCase so Windows
    // paths like `Node_Modules` or `.Git` are still recognised.
    private static readonly HashSet<string> FileSearchSkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "obj", "bin", "dist", "build", "target",
        ".idea", ".vs", ".vscode", ".gradle", ".next", "__pycache__",
        ".venv", "venv", ".cache", ".jiangyu", ".unity"
    };

    private const int FileSearchMaxResults = 10_000;

    [McpTool("jiangyu_list_directory",
        "List files and subdirectories in a project directory. Returns array of {name, path, isDirectory, isIgnored, size} entries. size is in bytes (files only). Path must be inside the open project.")]
    [McpParam("path", "string", "Absolute path to the directory to list.", Required = true)]
    internal static JsonElement ListDirectory(JsonElement? parameters)
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

    [McpTool("jiangyu_list_all_files",
        "List all files in the project tree (gitignore-aware). Returns array of relative file paths. Max 10,000 results. Path must be inside the open project.")]
    [McpParam("path", "string", "Absolute path to the project root directory.", Required = true)]
    internal static JsonElement ListAllFiles(JsonElement? parameters)
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

    /// <summary>
    /// Reads a UTF-8 text file from inside the project. Optional 1-indexed
    /// <c>startLine</c>/<c>endLine</c> windowing for huge files. Not exposed
    /// via MCP — ACP agents use <c>fs/read_text_file</c> instead — but the
    /// Host RPC and tests both go through here.
    /// </summary>
    public static JsonElement ReadFile(JsonElement? parameters)
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

    // 64 MiB — generous enough for any plausible source file, small enough that
    // a pathological paste-into-Monaco-and-save doesn't OOM the host.
    private const int MaxWriteFileBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Atomic UTF-8 write to a project file. Stages in a sibling tmp file
    /// then renames over the target so a mid-write crash doesn't corrupt
    /// the original. Not exposed via MCP — ACP agents use
    /// <c>fs/write_text_file</c> instead. Studio.Host's wrapper also tells
    /// its <c>ProjectWatcher</c> to suppress the resulting watcher event
    /// for the calling window so it doesn't see its own save as a conflict.
    /// </summary>
    public static JsonElement WriteFile(JsonElement? parameters)
    {
        var path = RequireString(parameters, "path");
        var content = RequireString(parameters, "content");
        EnsurePathInsideProject(path);

        // UTF-8 upper bound: 4 bytes per char. Fast reject before we allocate.
        if (content.Length > MaxWriteFileBytes)
            throw new IOException($"File content exceeds {MaxWriteFileBytes / (1024 * 1024)} MiB limit");

        // Atomic write: stage in a sibling tmp file, then rename over the
        // target. File.WriteAllText truncates-then-writes in place; a
        // mid-write crash leaves a corrupt file, which for an editor is
        // real data loss. Studio.Host's wrapper calls
        // ProjectWatcher.SuppressFor for both target and tmp BEFORE
        // invoking us, so a same-window save doesn't fire its own
        // conflict banner; the standalone Mcp binary has no watcher to
        // suppress.
        var tmp = path + ".jiangyu.tmp";
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
    internal static JsonElement MovePath(JsonElement? parameters)
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
    internal static JsonElement CopyPath(JsonElement? parameters)
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
    internal static JsonElement DeletePath(JsonElement? parameters)
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
    internal static JsonElement CreateFile(JsonElement? parameters)
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
    internal static JsonElement EditFile(JsonElement? parameters)
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

        return NullElement;
    }

    [McpTool("jiangyu_create_directory",
        "Create a directory in the project. Fails if the path already exists. Path must be inside the open project.")]
    [McpParam("path", "string", "Absolute path for the new directory.", Required = true)]
    internal static JsonElement CreateDirectory(JsonElement? parameters)
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
    internal static JsonElement GrepFiles(JsonElement? parameters)
    {
        var pattern = RequireString(parameters, "pattern");
        var root = TryGetString(parameters, "path") ?? RpcContext.ProjectRoot
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

    // Cache the game Unity version per resolved game-data path. DetectGameVersion
    // loads the AssetRipper GameStructure which is measurable-seconds slow; the
    // welcome screen + settings modal both poll getConfigStatus on open, so
    // without a cache every open would stall the UI.
    private static string? _cachedUnityVersionGamePath;
    private static UnityVersion? _cachedUnityGameVersion;

    internal static UnityVersion? GetGameUnityVersionCached(string gameDataPath)
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
    internal static JsonElement GetConfigStatus(JsonElement? __)
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
}
