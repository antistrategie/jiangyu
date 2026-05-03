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
                SafeSend(window, "compileFinished", new RpcHandlers.CompileFinishedEvent
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

            SafeSend(window, "compileFinished", new RpcHandlers.CompileFinishedEvent
            {
                Success = result.Success,
                BundlePath = result.BundlePath,
                ErrorMessage = result.ErrorMessage,
            });
        }
        catch (Exception ex)
        {
            SafeSend(window, "compileFinished", new RpcHandlers.CompileFinishedEvent
            {
                Success = false,
                ErrorMessage = $"{ex.GetType().Name}: {ex.Message}",
            });
        }
        finally
        {
            lock (RpcHandlers.CompileLock) RpcHandlers.CompileRunning = false;
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
}
