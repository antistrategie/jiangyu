using System.Diagnostics;
using Jiangyu.Acp;
using Jiangyu.Acp.JsonRpc;
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
    private InitializeResponse? _initializeResponse;
    private readonly Lock _lock = new();

    /// <summary>The MCP server port so we can tell the agent where to connect.</summary>
    public int McpPort { get; set; }

    /// <summary>
    /// Invoked when the agent process exits unexpectedly. Called on a
    /// background thread; the handler should be safe to call from any context.
    /// </summary>
    public Action? OnProcessExited { get; set; }

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

        // Drain stderr continuously and log it.
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                    Console.Error.WriteLine($"[Agent stderr] {line}");
            }
            catch { /* process exited */ }
        });

        var client = new AcpClient(
            handler,
            process.StandardOutput.BaseStream,
            process.StandardInput.BaseStream,
            FramingMode.NdJson);
        client.Start();

        Console.Error.WriteLine("[Agent] Listen loop started, sending initialize…");

        lock (_lock)
        {
            _process = process;
            _client = client;
        }

        // Monitor for unexpected process exit.
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            Console.Error.WriteLine($"[Agent] Process exited (code {(process.HasExited ? process.ExitCode : -1)})");
            OnProcessExited?.Invoke();
        };

        // Initialise the ACP connection. Wrap so an init failure tears down
        // the process; otherwise the caller would see the exception but
        // _process and _client would still be holding the orphan.
        InitializeResponse response;
        try
        {
            Console.Error.WriteLine("[Agent] Sending initialize request…");
            response = await client.InitializeAsync(new InitializeRequest
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
            Console.Error.WriteLine($"[Agent] Initialize response received: {response.AgentInfo?.Name}");
        }
        catch
        {
            Stop();
            throw;
        }

        // Spec: agent MUST echo the requested version if supported, otherwise
        // respond with the latest it supports. Treat any mismatch as fatal.
        if (response.ProtocolVersion != 1)
        {
            Stop();
            throw new InvalidOperationException(
                $"Agent reported unsupported ACP protocol version {response.ProtocolVersion}; expected 1.");
        }

        lock (_lock) _initializeResponse = response;
        return response;
    }

    /// <summary>
    /// Creates a new session, passing the MCP server config so the agent
    /// can discover Jiangyu tools.
    /// </summary>
    public async Task<NewSessionResponse> NewSessionAsync(string projectRoot, CancellationToken ct = default)
    {
        var client = Client ?? throw new InvalidOperationException("Agent not running.");
        InitializeResponse? init;
        lock (_lock) init = _initializeResponse;

        // Per spec, HTTP MCP is only legal when the agent advertises
        // mcpCapabilities.http; otherwise we omit the MCP server entirely.
        // Future: spawn a stdio MCP adapter for agents that don't support
        // HTTP, so the Jiangyu tool surface is reachable from any agent.
        var supportsHttpMcp = init?.AgentCapabilities?.McpCapabilities?.Http == true;
        var mcpServers = supportsHttpMcp
            ? new[]
            {
                new McpServerConfig
                {
                    Name = "jiangyu",
                    Type = "http",
                    Url = $"http://127.0.0.1:{McpPort}/mcp",
                    Headers = [],
                },
            }
            : Array.Empty<McpServerConfig>();

        var response = await client.NewSessionAsync(new NewSessionRequest
        {
            Cwd = projectRoot,
            McpServers = mcpServers,
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
            Prompt = [new TextContentBlock { Text = text }],
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
