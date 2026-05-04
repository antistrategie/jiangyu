using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniFrame;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Compile;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;
using Jiangyu.Shared;
using Jiangyu.Studio.Rpc;

namespace Jiangyu.Studio.Host.Rpc;

public static partial class RpcDispatcher
{
    // Kicks off CompilationService on a worker thread and streams progress/log
    // events back as host-pushed notifications. Returns immediately with
    // { started: true }; the `compileFinished` notification carries the result.
    // Rejects concurrent compiles — the UI layer should already gate on state,
    // but a stray second call would otherwise interleave sink output. Shares
    // the compile gate with the blocking MCP variant in Studio.Rpc.
    private static JsonElement HandleCompile(IInfiniFrameWindow window, JsonElement? unused)
    {
        _ = unused;
        var projectRoot = ProjectWatcher.ProjectRoot
            ?? throw new InvalidOperationException("No project open.");

        lock (RpcHandlers.CompileLock)
        {
            if (RpcHandlers.CompileRunning)
                throw new InvalidOperationException("Compile already in progress.");
            RpcHandlers.CompileRunning = true;
        }

        // Fire-and-forget: compile runs on a worker thread and streams events
        // back via notifications. The RPC returns immediately so the message
        // loop isn't blocked for the minutes a Unity build can take.
        _ = Task.Run(() => RunCompileAsync(BroadcastSink, projectRoot));
        return JsonSerializer.SerializeToElement(new CompileStartedAck { Started = true });
    }

    /// <summary>
    /// Wires <see cref="RpcHandlers.CompileRunOverride"/> so MCP-triggered
    /// compiles (e.g. agent calling <c>jiangyu_compile</c> via HTTP MCP)
    /// flow through the streaming pipeline. The override blocks the agent's
    /// MCP request thread until the build completes — that's expected for a
    /// blocking tool — while broadcasting <c>compileStarted</c> /
    /// <c>compileProgress</c> / <c>compileFinished</c> to every subscribed
    /// WebView so the CompileModal shows live progress. Called once at host
    /// startup; the standalone <c>jiangyu-mcp</c> binary doesn't register
    /// the override and falls back to the inline blocking path.
    /// </summary>
    public static void RegisterCompileOverride()
    {
        RpcHandlers.CompileRunOverride = projectRoot =>
        {
            lock (RpcHandlers.CompileLock)
            {
                if (RpcHandlers.CompileRunning)
                    throw new InvalidOperationException("Compile already in progress.");
                RpcHandlers.CompileRunning = true;
            }
            // Block this thread (an MCP request handler thread, NOT the
            // WebView dispatch thread) until the streaming task finishes,
            // so the agent's tool-call response carries the final result.
            return RunCompileAsync(BroadcastSink, projectRoot)
                .GetAwaiter().GetResult();
        };
    }

    /// <summary>
    /// Sink target that broadcasts to every currently-subscribed WebView
    /// rather than a single window. Used by both the UI-triggered compile
    /// (one window) and the MCP-triggered override (broadcast to whichever
    /// windows the modder has open).
    /// </summary>
    private static void BroadcastSink(string method, object payload)
    {
        foreach (var window in ProjectWatcher.SubscribedWindows())
        {
            try { SendNotification(window, method, payload); }
            catch (Exception ex) { Console.Error.WriteLine($"[Compile] Failed to send {method}: {ex.Message}"); }
        }
    }

    private static async Task<JsonElement> RunCompileAsync(Action<string, object> send, string projectRoot)
    {
        try
        {
            send("compileStarted", new CompileStartedEvent { ProjectRoot = projectRoot });

            var manifestPath = Path.Combine(projectRoot, ModManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                var notFound = new RpcHandlers.CompileFinishedEvent
                {
                    Success = false,
                    ErrorMessage = $"{ModManifest.FileName} not found. Run 'jiangyu init' first.",
                };
                send("compileFinished", notFound);
                return JsonSerializer.SerializeToElement(notFound);
            }

            var manifest = ModManifest.FromJson(await File.ReadAllTextAsync(manifestPath));
            var config = GlobalConfig.Load();

            var log = new StreamingLogSink(send);
            var progress = new StreamingProgressSink(send);

            var service = new CompilationService(log, progress);
            var result = await service.CompileAsync(new CompilationInput
            {
                Manifest = manifest,
                Config = config,
                ProjectDirectory = projectRoot,
            });

            var finished = new RpcHandlers.CompileFinishedEvent
            {
                Success = result.Success,
                BundlePath = result.BundlePath,
                ErrorMessage = result.ErrorMessage,
            };
            send("compileFinished", finished);
            return JsonSerializer.SerializeToElement(finished);
        }
        catch (Exception ex)
        {
            var failed = new RpcHandlers.CompileFinishedEvent
            {
                Success = false,
                ErrorMessage = $"{ex.GetType().Name}: {ex.Message}",
            };
            send("compileFinished", failed);
            return JsonSerializer.SerializeToElement(failed);
        }
        finally
        {
            lock (RpcHandlers.CompileLock) RpcHandlers.CompileRunning = false;
        }
    }

    private sealed class StreamingLogSink(Action<string, object> send) : ILogSink
    {
        public void Info(string message) => Push("info", message);
        public void Warning(string message) => Push("warn", message);
        public void Error(string message) => Push("error", message);

        private void Push(string level, string message)
            => send("compileLog", new CompileLogEventPayload { Level = level, Message = message });
    }

    // Throttles ReportProgress to ~20/s so index loads don't flood the message
    // channel. Phase/status/finish events bypass the throttle; a completion
    // tick (current==total) is always sent so the bar lands on full.
    private sealed class StreamingProgressSink(Action<string, object> send) : IProgressSink
    {
        private const long ProgressThrottleTicks = TimeSpan.TicksPerMillisecond * 50;
        private long _lastProgressTicks;

        public void SetPhase(string phase)
            => send("compilePhase", new CompilePhaseEvent { Phase = phase });

        public void SetStatus(string status)
            => send("compileStatus", new CompileStatusEvent { Status = status });

        public void ReportProgress(int current, int total)
        {
            var now = DateTime.UtcNow.Ticks;
            if (current < total && now - _lastProgressTicks < ProgressThrottleTicks)
                return;
            _lastProgressTicks = now;
            send("compileProgress", new CompileProgressEvent { Current = current, Total = total });
        }

        public void Finish()
            => send("compileProgress", new CompileProgressEvent { Current = 0, Total = 0 });
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
}
