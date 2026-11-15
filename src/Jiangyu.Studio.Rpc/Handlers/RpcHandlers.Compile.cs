using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Compile;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;
using Jiangyu.Core.Rpc;

namespace Jiangyu.Studio.Rpc;

public static partial class RpcHandlers
{
    /// <summary>
    /// Cross-handler compile gate. Both the blocking MCP tool here and the
    /// streaming WebView RPC in Studio.Host route through the shared build
    /// gate (<see cref="BeginBuildOp"/>) so a second compile — or a package
    /// or deploy — can't start while one is already in flight.
    /// </summary>

    /// <summary>
    /// Optional override that lets Studio.Host route MCP-triggered compiles
    /// through its streaming pipeline (so the CompileModal shows progress
    /// when the agent runs <c>jiangyu_compile</c>). Set at host startup;
    /// null in the standalone <c>jiangyu-mcp</c> binary, where compiles
    /// run inline without a UI to notify. Returns the same shape as the
    /// inline path so the agent gets a typed result either way. Takes the
    /// project root and is responsible for the build gate
    /// (<see cref="BeginBuildOp"/>/<see cref="EndBuildOp"/>) since it owns
    /// the streaming state.
    /// </summary>
    public static Func<string, JsonElement>? CompileRunOverride;

    [McpTool("jiangyu_compile",
        "Compile the current project. Blocking; waits until the build finishes. Returns {success, bundlePath?, errorMessage?}. Rejects if a compile is already running. Requires an open project.",
        LongRunning = true)]
    internal static JsonElement CompileBlocking(JsonElement? __)
    {
        var projectRoot = RpcContext.ProjectRoot
            ?? throw new InvalidOperationException("No project open.");

        // Studio.Host injects a streaming runner; defer when present so
        // the modder sees progress in the UI's CompileModal even though
        // the agent triggered the build.
        if (CompileRunOverride is { } streaming)
            return streaming(projectRoot);

        BeginBuildOp();

        try
        {
            var manifestPath = Path.Combine(projectRoot, ModManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                return JsonSerializer.SerializeToElement(new CompileFinishedEvent
                {
                    Success = false,
                    ErrorMessage = $"{ModManifest.FileName} not found. Run 'jiangyu init' first.",
                });
            }

            var manifest = ModManifest.FromJson(File.ReadAllText(manifestPath));
            var config = GlobalConfig.Load();

            var log = NullLogSink.Instance;
            var progress = NullProgressSink.Instance;

            var service = new CompilationService(log, progress);
            var result = service.CompileAsync(new CompilationInput
            {
                Manifest = manifest,
                Config = config,
                ProjectDirectory = projectRoot,
            }).GetAwaiter().GetResult();

            return JsonSerializer.SerializeToElement(new CompileFinishedEvent
            {
                Success = result.Success,
                BundlePath = result.BundlePath,
                ErrorMessage = result.ErrorMessage,
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.SerializeToElement(new CompileFinishedEvent
            {
                Success = false,
                ErrorMessage = $"{ex.GetType().Name}: {ex.Message}",
            });
        }
        finally
        {
            EndBuildOp();
        }
    }

    // Counts asset and template files for the currently
    // open project so the compile modal can show a pre-compile dossier without
    // running the pipeline. Scans directly — cheaper than spinning up the full
    // CompilationService just to count files.
    [McpTool("jiangyu_compile_summary",
        "Get a pre-compile dossier for the current project: mod name, version, author, and counts of model, texture, sprite and audio assets, template files, template patches, and template clones. Requires an open project.")]
    internal static JsonElement GetCompileSummary(JsonElement? __)
    {
        var projectRoot = RpcContext.ProjectRoot
            ?? throw new InvalidOperationException("No project open.");

        string? modName = null;
        string? modVersion = null;
        string? modAuthor = null;
        var manifestPath = Path.Combine(projectRoot, ModManifest.FileName);
        if (File.Exists(manifestPath))
        {
            try
            {
                var manifest = ModManifest.FromJson(File.ReadAllText(manifestPath));
                modName = manifest.Name;
                modVersion = manifest.Version;
                modAuthor = manifest.Author;
            }
            catch
            {
                // Malformed manifest — the compile will surface the error; here
                // we just leave the dossier fields blank and move on.
            }
        }

        var replacementsRoot = Path.Combine(projectRoot, "assets", "replacements");
        var templatesRoot = Path.Combine(projectRoot, "templates");
        var unityPrefabsRoot = Path.Combine(projectRoot, "unity", "Assets", "Prefabs");

        var summary = new CompileSummary
        {
            ModName = modName,
            ModVersion = modVersion,
            ModAuthor = modAuthor,
            // Per-type counts sum the replacement convention
            // (assets/replacements/<cat>/) and the addition convention
            // (assets/<cat>/ or assets/additions/<cat>/) so what the modal advertises matches
            // what the compile pipeline actually bundles. Models rolls in
            // prefab additions because both are GameObject-shaped 3D content
            // from the modder's perspective: model replacements (vanilla
            // mesh swaps) and prefab additions (new entities baked via
            // BakeHumanoid / dropped under unity/Assets/Prefabs/) share the
            // same stat tile.
            Models = CountSubdirs(Path.Combine(replacementsRoot, "models"))
                + Jiangyu.Shared.Replacements.AdditionPaths.CategoryDirs(projectRoot, "models").Sum(dir => CountSubdirs(dir))
                + CountMainPrefabs(unityPrefabsRoot)
                + Jiangyu.Shared.Replacements.AdditionPaths.CategoryDirs(projectRoot, "prefabs").Sum(dir => CountFiles(dir, "*.bundle")),
            Textures = CountFiles(Path.Combine(replacementsRoot, "textures"))
                + Jiangyu.Shared.Replacements.AdditionPaths.CategoryDirs(projectRoot, "textures").Sum(dir => CountFilesRecursive(dir)),
            Sprites = CountFiles(Path.Combine(replacementsRoot, "sprites"))
                + Jiangyu.Shared.Replacements.AdditionPaths.CategoryDirs(projectRoot, "sprites").Sum(dir => CountFilesRecursive(dir)),
            Audio = CountFiles(Path.Combine(replacementsRoot, "audio"))
                + Jiangyu.Shared.Replacements.AdditionPaths.CategoryDirs(projectRoot, "audio").Sum(dir => CountFilesRecursive(dir)),
            TemplateFiles = CountFilesRecursive(templatesRoot, "*.kdl"),
            TemplatePatches = CountKdlNodes(templatesRoot, "patch"),
            TemplateClones = CountKdlNodes(templatesRoot, "clone"),
        };

        return JsonSerializer.SerializeToElement(summary);
    }

    private static int CountSubdirs(string dir)
        => Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).Count() : 0;

    private static int CountFiles(string dir, string searchPattern = "*")
        => Directory.Exists(dir) ? Directory.EnumerateFiles(dir, searchPattern).Count() : 0;

    private static int CountFilesRecursive(string dir, string searchPattern = "*")
        => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, searchPattern, SearchOption.AllDirectories)
                .Where(p => !p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                .Count()
            : 0;

    // Counts `main.prefab` files anywhere under unityPrefabsRoot — the bake
    // convention emits unity/Assets/Prefabs/<cat>/<name>/main.prefab per
    // entity, so this matches the count of prefab bundles Unity will build.
    // Pre-built `.bundle` escape-hatch additions are counted separately.
    private static int CountMainPrefabs(string dir)
        => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "main.prefab", SearchOption.AllDirectories).Count()
            : 0;

    // Heuristic line counter. Good enough for the compile status badge — not
    // a parser. Skips leading whitespace, `//` comments, and `/-` slashdash,
    // and requires whitespace or `{` after the node name so `patchwork`
    // doesn't match `patch`. Still misses multiple nodes on one line.
    private static int CountKdlNodes(string dir, string nodeType)
    {
        if (!Directory.Exists(dir)) return 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.kdl", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.AsSpan().TrimStart();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                if (trimmed.StartsWith("/-", StringComparison.Ordinal)) continue;
                if (!trimmed.StartsWith(nodeType, StringComparison.Ordinal)) continue;
                if (trimmed.Length == nodeType.Length) continue;
                var tail = trimmed[nodeType.Length];
                if (char.IsWhiteSpace(tail) || tail == '{') count++;
            }
        }
        return count;
    }

}

[RpcType]
public sealed class CompileFinishedEvent
{
    [JsonPropertyName("success")]
    public required bool Success { get; set; }

    [JsonPropertyName("bundlePath")]
    public string? BundlePath { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

[RpcType]
public sealed class CompileSummary
{
    [JsonPropertyName("modName")]
    public string? ModName { get; set; }

    [JsonPropertyName("modVersion")]
    public string? ModVersion { get; set; }

    [JsonPropertyName("modAuthor")]
    public string? ModAuthor { get; set; }

    [JsonPropertyName("models")]
    public required int Models { get; set; }

    [JsonPropertyName("textures")]
    public required int Textures { get; set; }

    [JsonPropertyName("sprites")]
    public required int Sprites { get; set; }

    [JsonPropertyName("audio")]
    public required int Audio { get; set; }

    [JsonPropertyName("templateFiles")]
    public required int TemplateFiles { get; set; }

    [JsonPropertyName("templatePatches")]
    public required int TemplatePatches { get; set; }

    [JsonPropertyName("templateClones")]
    public required int TemplateClones { get; set; }
}
