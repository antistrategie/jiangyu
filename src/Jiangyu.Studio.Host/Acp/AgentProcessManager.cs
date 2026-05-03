using System.Diagnostics;
using Jiangyu.Acp;
using Jiangyu.Acp.Schema;

namespace Jiangyu.Studio.Host.Acp;

/// <summary>
/// Manages agent subprocess lifecycle: spawn, connect via ACP, and tear down.
/// One active agent at a time (the UI enforces this).
/// </summary>
internal sealed class AgentProcessManager : IDisposable
{
    private Process? _process;
    private AcpClient? _client;
    private string? _sessionId;
    private readonly Lock _lock = new();

    /// <summary>The MCP server port so we can tell the agent where to connect.</summary>
    public int McpPort { get; set; }

    public bool IsRunning
    {
        get
        {
            lock (_lock) return _process is { HasExited: false };
        }
    }

    public string? SessionId
    {
        get { lock (_lock) return _sessionId; }
    }

    public AcpClient? Client
    {
        get { lock (_lock) return _client; }
    }

    /// <summary>
    /// Spawns the agent subprocess and initialises the ACP connection.
    /// </summary>
    public async Task<InitializeResponse> StartAsync(
        string command,
        string[] args,
        IAcpClientHandler handler,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_process is { HasExited: false })
                throw new InvalidOperationException("Agent already running.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start agent: {command}");

        // Drain stderr continuously. The pipe buffer is a few KB; if no one
        // reads, the agent blocks on its first error log and the whole ACP
        // session deadlocks.
        _ = Task.Run(async () =>
        {
            try
            {
                var buf = new char[4096];
                while (await process.StandardError.ReadAsync(buf).ConfigureAwait(false) > 0) { }
            }
            catch { /* process exited */ }
        });

        var client = new AcpClient(
            handler,
            process.StandardOutput.BaseStream,
            process.StandardInput.BaseStream);
        client.Start();

        lock (_lock)
        {
            _process = process;
            _client = client;
        }

        // Initialise the ACP connection.
        var response = await client.InitializeAsync(new InitializeRequest
        {
            ProtocolVersion = 1,
            ClientCapabilities = new ClientCapabilities
            {
                Fs = new FileSystemCapability
                {
                    ReadTextFile = true,
                    WriteTextFile = true,
                },
                Terminal = true,
            },
            ClientInfo = new Implementation
            {
                Name = "jiangyu-studio",
                Title = "Jiangyu Studio",
                Version = typeof(AgentProcessManager).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            },
        }, ct);

        return response;
    }

    /// <summary>
    /// Creates a new session, passing the MCP server config so the agent
    /// can discover Jiangyu tools.
    /// </summary>
    public async Task<NewSessionResponse> NewSessionAsync(string projectRoot, CancellationToken ct = default)
    {
        var client = Client ?? throw new InvalidOperationException("Agent not running.");

        var response = await client.NewSessionAsync(new NewSessionRequest
        {
            Cwd = projectRoot,
            McpServers =
            [
                new McpServerConfig
                {
                    Name = "jiangyu",
                    Uri = $"http://127.0.0.1:{McpPort}/mcp",
                },
            ],
        }, ct);

        lock (_lock) _sessionId = response.SessionId;
        return response;
    }

    /// <summary>
    /// Sends a prompt message to the current session.
    /// </summary>
    public async Task<PromptResponse> PromptAsync(string text, CancellationToken ct = default)
    {
        var client = Client ?? throw new InvalidOperationException("Agent not running.");
        var sessionId = SessionId ?? throw new InvalidOperationException("No active session.");

        return await client.PromptAsync(new PromptRequest
        {
            SessionId = sessionId,
            Content = [new TextContentBlock { Text = text }],
        }, ct);
    }

    /// <summary>
    /// Cancels the current in-progress prompt.
    /// </summary>
    public async Task CancelAsync(CancellationToken ct = default)
    {
        var client = Client;
        var sessionId = SessionId;
        if (client is null || sessionId is null) return;

        await client.CancelAsync(sessionId, ct);
    }

    /// <summary>
    /// Closes the current session.
    /// </summary>
    public async Task CloseSessionAsync(CancellationToken ct = default)
    {
        var client = Client;
        var sessionId = SessionId;
        if (client is null || sessionId is null) return;

        await client.CloseSessionAsync(new CloseSessionRequest { SessionId = sessionId }, ct);
        lock (_lock) _sessionId = null;
    }

    /// <summary>
    /// Stops the agent subprocess and releases all resources.
    /// </summary>
    public void Stop()
    {
        Process? process;
        AcpClient? client;

        lock (_lock)
        {
            process = _process;
            client = _client;
            _process = null;
            _client = null;
            _sessionId = null;
        }

        client?.Dispose();

        if (process is { HasExited: false })
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }

        process?.Dispose();
    }

    public void Dispose() => Stop();
}
