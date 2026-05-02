using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniFrame;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Compile;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;
using Jiangyu.Shared;

namespace Jiangyu.Studio.Host.Rpc;

public static partial class RpcDispatcher
{
    private static readonly Lock CompileLock = new();
    private static bool _compileRunning;

    // Counts asset replacements, additions, and template files for the currently
    // open project so the compile modal can show a pre-compile dossier without
    // running the pipeline. Scans directly — cheaper than spinning up the full
    // CompilationService just to count files.
    [McpTool("jiangyu_compile_summary",
        "Get a pre-compile dossier for the current project: mod name, version, author, and counts of model/texture/sprite/audio replacements, addition files, template files, template patches, and template clones. No parameters. Requires an open project.")]
    private static JsonElement HandleGetCompileSummary(IInfiniFrameWindow _, JsonElement? __)
    {
        var projectRoot = ProjectWatcher.ProjectRoot
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
        var additionsRoot = Path.Combine(projectRoot, "assets", "additions");
        var templatesRoot = Path.Combine(projectRoot, "templates");

        var summary = new CompileSummary
        {
            ModName = modName,
            ModVersion = modVersion,
            ModAuthor = modAuthor,
            ModelReplacements = CountSubdirs(Path.Combine(replacementsRoot, "models")),
            TextureReplacements = CountFiles(Path.Combine(replacementsRoot, "textures")),
            SpriteReplacements = CountFiles(Path.Combine(replacementsRoot, "sprites")),
            AudioReplacements = CountFiles(Path.Combine(replacementsRoot, "audio")),
            AdditionFiles = CountFilesRecursive(additionsRoot),
            TemplateFiles = CountFilesRecursive(templatesRoot, "*.kdl"),
            TemplatePatches = CountKdlNodes(templatesRoot, "patch"),
            TemplateClones = CountKdlNodes(templatesRoot, "clone"),
        };

        return JsonSerializer.SerializeToElement(summary);
    }

    private static int CountSubdirs(string dir)
        => Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).Count() : 0;

    private static int CountFiles(string dir)
        => Directory.Exists(dir) ? Directory.EnumerateFiles(dir).Count() : 0;

    private static int CountFilesRecursive(string dir, string searchPattern = "*")
        => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, searchPattern, SearchOption.AllDirectories).Count()
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

    // Kicks off CompilationService on a worker thread and streams progress/log
    // events back as host-pushed notifications. Returns immediately with
    // { started: true }; the `compileFinished` notification carries the result.
    // Rejects concurrent compiles — the UI layer should already gate on state,
    // but a stray second call would otherwise interleave sink output.
    private static JsonElement HandleCompile(IInfiniFrameWindow window, JsonElement? unused)
    {
        _ = unused;
        var projectRoot = ProjectWatcher.ProjectRoot
            ?? throw new InvalidOperationException("No project open.");

        lock (CompileLock)
        {
            if (_compileRunning)
                throw new InvalidOperationException("Compile already in progress.");
            _compileRunning = true;
        }

        // Fire-and-forget: compile runs on a worker thread and streams events
        // back via notifications. The RPC returns immediately so the message
        // loop isn't blocked for the minutes a Unity build can take.
        _ = Task.Run(() => RunCompileAsync(window, projectRoot));
        return JsonSerializer.SerializeToElement(new CompileStartedAck { Started = true });
    }

    private static async Task RunCompileAsync(IInfiniFrameWindow window, string projectRoot)
    {
        try
        {
            SafeSend(window, "compileStarted", new CompileStartedEvent { ProjectRoot = projectRoot });

            var manifestPath = Path.Combine(projectRoot, ModManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                SafeSend(window, "compileFinished", new CompileFinishedEvent
                {
                    Success = false,
                    ErrorMessage = $"{ModManifest.FileName} not found. Run 'jiangyu init' first.",
                });
                return;
            }

            var manifest = ModManifest.FromJson(await File.ReadAllTextAsync(manifestPath));
            var config = GlobalConfig.Load();

            var log = new StreamingLogSink(window);
            var progress = new StreamingProgressSink(window);

            var service = new CompilationService(log, progress);
            var result = await service.CompileAsync(new CompilationInput
            {
                Manifest = manifest,
                Config = config,
                ProjectDirectory = projectRoot,
            });

            SafeSend(window, "compileFinished", new CompileFinishedEvent
            {
                Success = result.Success,
                BundlePath = result.BundlePath,
                ErrorMessage = result.ErrorMessage,
            });
        }
        catch (Exception ex)
        {
            SafeSend(window, "compileFinished", new CompileFinishedEvent
            {
                Success = false,
                ErrorMessage = $"{ex.GetType().Name}: {ex.Message}",
            });
        }
        finally
        {
            lock (CompileLock) _compileRunning = false;
        }
    }

    private static void SafeSend(IInfiniFrameWindow window, string method, object payload)
    {
        try { SendNotification(window, method, payload); }
        catch (Exception ex) { Console.Error.WriteLine($"[Compile] Failed to send {method}: {ex.Message}"); }
    }

    private sealed class StreamingLogSink(IInfiniFrameWindow window) : ILogSink
    {
        public void Info(string message) => Push("info", message);
        public void Warning(string message) => Push("warn", message);
        public void Error(string message) => Push("error", message);

        private void Push(string level, string message)
            => SafeSend(window, "compileLog", new CompileLogEventPayload { Level = level, Message = message });
    }

    // Throttles ReportProgress to ~20/s so index loads don't flood the message
    // channel. Phase/status/finish events bypass the throttle; a completion
    // tick (current==total) is always sent so the bar lands on full.
    private sealed class StreamingProgressSink(IInfiniFrameWindow window) : IProgressSink
    {
        private const long ProgressThrottleTicks = TimeSpan.TicksPerMillisecond * 50;
        private long _lastProgressTicks;

        public void SetPhase(string phase)
            => SafeSend(window, "compilePhase", new CompilePhaseEvent { Phase = phase });

        public void SetStatus(string status)
            => SafeSend(window, "compileStatus", new CompileStatusEvent { Status = status });

        public void ReportProgress(int current, int total)
        {
            var now = DateTime.UtcNow.Ticks;
            if (current < total && now - _lastProgressTicks < ProgressThrottleTicks)
                return;
            _lastProgressTicks = now;
            SafeSend(window, "compileProgress", new CompileProgressEvent { Current = current, Total = total });
        }

        public void Finish()
            => SafeSend(window, "compileProgress", new CompileProgressEvent { Current = 0, Total = 0 });
    }

    [RpcType]
    internal sealed class CompileStartedAck
    {
        [JsonPropertyName("started")]
        public required bool Started { get; set; }
    }

    [RpcType]
    internal sealed class CompileStartedEvent
    {
        [JsonPropertyName("projectRoot")]
        public required string ProjectRoot { get; set; }
    }

    [RpcType]
    internal sealed class CompilePhaseEvent
    {
        [JsonPropertyName("phase")]
        public required string Phase { get; set; }
    }

    [RpcType]
    internal sealed class CompileStatusEvent
    {
        [JsonPropertyName("status")]
        public required string Status { get; set; }
    }

    [RpcType]
    internal sealed class CompileProgressEvent
    {
        [JsonPropertyName("current")]
        public required int Current { get; set; }

        [JsonPropertyName("total")]
        public required int Total { get; set; }
    }

    [RpcType]
    internal sealed class CompileLogEventPayload
    {
        [JsonPropertyName("level")]
        public required string Level { get; set; }

        [JsonPropertyName("message")]
        public required string Message { get; set; }
    }

    [RpcType]
    internal sealed class CompileFinishedEvent
    {
        [JsonPropertyName("success")]
        public required bool Success { get; set; }

        [JsonPropertyName("bundlePath")]
        public string? BundlePath { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }
    }

    [RpcType]
    internal sealed class CompileSummary
    {
        [JsonPropertyName("modName")]
        public string? ModName { get; set; }

        [JsonPropertyName("modVersion")]
        public string? ModVersion { get; set; }

        [JsonPropertyName("modAuthor")]
        public string? ModAuthor { get; set; }

        [JsonPropertyName("modelReplacements")]
        public required int ModelReplacements { get; set; }

        [JsonPropertyName("textureReplacements")]
        public required int TextureReplacements { get; set; }

        [JsonPropertyName("spriteReplacements")]
        public required int SpriteReplacements { get; set; }

        [JsonPropertyName("audioReplacements")]
        public required int AudioReplacements { get; set; }

        [JsonPropertyName("additionFiles")]
        public required int AdditionFiles { get; set; }

        [JsonPropertyName("templateFiles")]
        public required int TemplateFiles { get; set; }

        [JsonPropertyName("templatePatches")]
        public required int TemplatePatches { get; set; }

        [JsonPropertyName("templateClones")]
        public required int TemplateClones { get; set; }
    }
}
