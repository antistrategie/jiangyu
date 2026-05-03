using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using InfiniFrame;
using Jiangyu.Acp;
using Jiangyu.Acp.Schema;
using Jiangyu.Studio.Host.Rpc;

namespace Jiangyu.Studio.Host.Acp;

/// <summary>
/// Implements <see cref="IAcpClientHandler"/> by bridging agent callbacks to
/// existing RPC handlers and forwarding updates to the frontend.
/// </summary>
internal sealed class AcpClientHandler : IAcpClientHandler
{
    private readonly IInfiniFrameWindow _window;
    private readonly ConcurrentDictionary<string, TerminalState> _terminals = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RequestPermissionResponse>> _pendingPermissions = new();

    public AcpClientHandler(IInfiniFrameWindow window)
    {
        _window = window;
    }

    public ValueTask<ReadTextFileResponse> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken ct)
    {
        RpcDispatcher.EnsurePathInsideProject(request.Path);

        var text = File.ReadAllText(request.Path);

        if (request.StartLine.HasValue || request.EndLine.HasValue)
        {
            var lines = text.Split('\n');
            var start = Math.Max(0, (request.StartLine ?? 1) - 1);
            var end = Math.Min(lines.Length, request.EndLine ?? lines.Length);
            text = string.Join('\n', lines[start..end]);
        }

        if (request.MaxCharacters.HasValue && text.Length > request.MaxCharacters.Value)
            text = text[..request.MaxCharacters.Value];

        return ValueTask.FromResult(new ReadTextFileResponse { Text = text });
    }

    public ValueTask<WriteTextFileResponse> WriteTextFileAsync(WriteTextFileRequest request, CancellationToken ct)
    {
        RpcDispatcher.EnsurePathInsideProject(request.Path);

        // Atomic write so a mid-write crash can't corrupt the user's file,
        // and suppress watcher events for this window so the editor doesn't
        // show a "changed externally" banner for its own agent's edit.
        var tmp = request.Path + ".jiangyu.tmp";
        ProjectWatcher.SuppressFor(request.Path, _window.Id);
        ProjectWatcher.SuppressFor(tmp, _window.Id);
        try
        {
            File.WriteAllText(tmp, request.Content);
            File.Move(tmp, request.Path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
        return ValueTask.FromResult(new WriteTextFileResponse { Success = true });
    }

    public ValueTask<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken ct)
    {
        // Push to frontend, wait for response.
        var id = request.ToolCallId ?? Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RequestPermissionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPermissions[id] = tcs;

        ct.Register(() =>
        {
            if (_pendingPermissions.TryRemove(id, out var removed))
                removed.TrySetCanceled(ct);
        });

        RpcDispatcher.SendNotification(_window, "agentPermissionRequest", new
        {
            permissionId = id,
            sessionId = request.SessionId,
            description = request.Description,
            options = request.Options,
        });

        return new ValueTask<RequestPermissionResponse>(tcs.Task);
    }

    /// <summary>
    /// Resolves a pending permission request. Called from the frontend RPC.
    /// </summary>
    public void ResolvePermission(string permissionId, PermissionOutcome outcome)
    {
        if (_pendingPermissions.TryRemove(permissionId, out var tcs))
            tcs.TrySetResult(new RequestPermissionResponse { Outcome = outcome });
    }

    // --- Terminal bridge ---

    public ValueTask<CreateTerminalResponse> CreateTerminalAsync(CreateTerminalRequest request, CancellationToken ct)
    {
        var terminalId = Guid.NewGuid().ToString("N");

        var psi = new ProcessStartInfo
        {
            FileName = request.Command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (request.Args is not null)
            foreach (var arg in request.Args)
                psi.ArgumentList.Add(arg);

        if (request.Cwd is not null)
            psi.WorkingDirectory = request.Cwd;

        if (request.Env is not null)
            foreach (var (key, value) in request.Env)
                psi.Environment[key] = value;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start terminal command: {request.Command}");

        var state = new TerminalState(process, request.MaxOutputBytes ?? 1_000_000);
        _terminals[terminalId] = state;

        // Capture output asynchronously.
        _ = Task.Run(() => state.CaptureOutputAsync(), ct);

        return ValueTask.FromResult(new CreateTerminalResponse { TerminalId = terminalId });
    }

    public ValueTask<TerminalOutputResponse> TerminalOutputAsync(TerminalOutputRequest request, CancellationToken ct)
    {
        var state = GetTerminal(request.TerminalId);
        return ValueTask.FromResult(new TerminalOutputResponse
        {
            Output = state.GetOutput(),
            IsTruncated = state.IsTruncated,
            ExitStatus = state.Process.HasExited
                ? new TerminalExitStatus { ExitCode = state.Process.ExitCode }
                : null,
        });
    }

    public async ValueTask<WaitForTerminalExitResponse> WaitForTerminalExitAsync(WaitForTerminalExitRequest request, CancellationToken ct)
    {
        var state = GetTerminal(request.TerminalId);
        await state.Process.WaitForExitAsync(ct);
        return new WaitForTerminalExitResponse
        {
            ExitStatus = new TerminalExitStatus { ExitCode = state.Process.ExitCode },
        };
    }

    public ValueTask<KillTerminalResponse> KillTerminalAsync(KillTerminalRequest request, CancellationToken ct)
    {
        var state = GetTerminal(request.TerminalId);
        if (!state.Process.HasExited)
        {
            try { state.Process.Kill(entireProcessTree: true); } catch { }
        }
        return ValueTask.FromResult(new KillTerminalResponse { Success = true });
    }

    public ValueTask<ReleaseTerminalResponse> ReleaseTerminalAsync(ReleaseTerminalRequest request, CancellationToken ct)
    {
        if (_terminals.TryRemove(request.TerminalId, out var state))
        {
            if (!state.Process.HasExited)
            {
                try { state.Process.Kill(entireProcessTree: true); } catch { }
            }
            state.Process.Dispose();
        }
        return ValueTask.FromResult(new ReleaseTerminalResponse { Success = true });
    }

    // --- Session updates ---

    public ValueTask OnSessionUpdateAsync(SessionNotification notification, CancellationToken ct)
    {
        RpcDispatcher.SendNotification(_window, "agentUpdate", notification);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Releases all terminal resources. Called when the agent stops.
    /// </summary>
    public void ReleaseAllTerminals()
    {
        foreach (var (id, state) in _terminals)
        {
            if (!state.Process.HasExited)
            {
                try { state.Process.Kill(entireProcessTree: true); } catch { }
            }
            state.Process.Dispose();
        }
        _terminals.Clear();
    }

    private TerminalState GetTerminal(string terminalId)
    {
        if (!_terminals.TryGetValue(terminalId, out var state))
            throw new InvalidOperationException($"Unknown terminal: {terminalId}");
        return state;
    }

    private sealed class TerminalState
    {
        private readonly StringBuilder _output = new();
        private readonly long _maxBytes;
        private long _totalBytes;

        public Process Process { get; }
        public bool IsTruncated { get; private set; }

        public TerminalState(Process process, long maxBytes)
        {
            Process = process;
            _maxBytes = maxBytes;
        }

        public async Task CaptureOutputAsync()
        {
            var stdoutTask = CaptureStreamAsync(Process.StandardOutput);
            var stderrTask = CaptureStreamAsync(Process.StandardError);
            await Task.WhenAll(stdoutTask, stderrTask);
        }

        private async Task CaptureStreamAsync(StreamReader stream)
        {
            var buffer = new char[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                lock (_output)
                {
                    if (_totalBytes >= _maxBytes)
                    {
                        IsTruncated = true;
                        continue;
                    }

                    _output.Append(buffer, 0, read);
                    _totalBytes += Encoding.UTF8.GetByteCount(buffer, 0, read);
                }
            }
        }

        public string GetOutput()
        {
            lock (_output) return _output.ToString();
        }
    }
}
