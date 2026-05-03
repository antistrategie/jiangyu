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

    /// <summary>
    /// Set by <c>Program.Main</c> at startup once the WebApp has bound. Both
    /// fields are required for the HTTP entry in <see cref="BuildMcpServerConfig"/>;
    /// when either is null we only send the stdio entry.
    /// </summary>
    public static string? HttpMcpUrl { get; set; }
    public static string? HttpMcpToken { get; set; }

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
            var caps = response.AgentCapabilities;
            var mcpCaps = caps?.McpCapabilities;
            Console.Error.WriteLine(
                $"[Agent] Initialised: {response.AgentInfo?.Name} v{response.AgentInfo?.Version} " +
                $"protocol={response.ProtocolVersion} " +
                $"loadSession={caps?.LoadSession ?? false} " +
                $"mcp.http={mcpCaps?.Http ?? false} mcp.sse={mcpCaps?.Sse ?? false}");
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

        var mcp = BuildMcpServerConfig();
        Console.Error.WriteLine($"[Agent] session/new cwd={projectRoot} mcpServers={DescribeMcpServers(mcp)}");

        var response = await client.NewSessionAsync(new NewSessionRequest
        {
            Cwd = projectRoot,
            McpServers = mcp,
        }, ct);

        lock (_lock) _sessionId = response.SessionId;
        return response;
    }

    /// <summary>
    /// Resumes an existing session by id. Per spec the agent streams
    /// historical session updates before this awaitable resolves, so
    /// callers can rely on "load complete" once it returns.
    /// </summary>
    public async Task<LoadSessionResponse> LoadSessionAsync(
        string sessionId,
        string projectRoot,
        CancellationToken ct = default)
    {
        var client = Client ?? throw new InvalidOperationException("Agent not running.");

        InitializeResponse? init;
        lock (_lock) init = _initializeResponse;
        if (init?.AgentCapabilities?.LoadSession != true)
            throw new InvalidOperationException("This agent does not support resuming sessions.");

        var response = await client.LoadSessionAsync(new LoadSessionRequest
        {
            SessionId = sessionId,
            Cwd = projectRoot,
            McpServers = BuildMcpServerConfig(),
        }, ct);

        lock (_lock) _sessionId = sessionId;
        return response;
    }

    /// <summary>
    /// Build the MCP server config we hand to the agent at session/new. We
    /// send both transports because no agent supports both — each one picks
    /// the entry it accepts and drops the other:
    /// <list type="bullet">
    ///   <item><description>stdio: <c>jiangyu-mcp</c> sibling exe.
    ///     Anthropic's claude-agent-sdk silently DROPS HTTP MCP entries
    ///     (despite advertising <c>mcpCapabilities.http=true</c>) and
    ///     only honours stdio.</description></item>
    ///   <item><description>http: the <c>/mcp</c> endpoint hosted by
    ///     Studio.Host with bearer-token auth. GitHub Copilot's ACP
    ///     integration silently REJECTS stdio entries
    ///     (<c>Rejecting non-http/sse MCP server</c> in its logs) and
    ///     only honours http/sse.</description></item>
    /// </list>
    /// Both share the name <c>jiangyu</c>; the agent only ends up with one
    /// after dropping the unsupported one, so the model sees a single
    /// unified server either way.
    /// </summary>
    internal static McpServerConfig[] BuildMcpServerConfig()
    {
        var configs = new List<McpServerConfig>();

        var exeName = OperatingSystem.IsWindows() ? "jiangyu-mcp.exe" : "jiangyu-mcp";
        var mcpPath = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(mcpPath))
        {
            configs.Add(new McpServerConfig
            {
                Name = "jiangyu",
                Command = mcpPath,
                Args = [],
                Env = [],
            });
        }
        else
        {
            Console.Error.WriteLine($"[Agent] jiangyu-mcp binary not found at {mcpPath}; stdio MCP disabled.");
        }

        if (HttpMcpUrl is { } url && HttpMcpToken is { } token)
        {
            configs.Add(new McpServerConfig
            {
                Name = "jiangyu",
                Type = "http",
                Url = url,
                Headers =
                [
                    new HttpHeader { Name = "Authorization", Value = $"Bearer {token}" },
                ],
            });
        }
        else
        {
            Console.Error.WriteLine("[Agent] HTTP MCP endpoint not bound; http MCP disabled.");
        }

        return [.. configs];
    }

    /// <summary>
    /// Renders the MCP server array for stderr logging. Skips the bearer
    /// token in HTTP headers — the value is a per-launch secret used to
    /// gate <c>/mcp</c> against unrelated processes on the same loopback,
    /// and dumping it to logs (which may be captured by the user's
    /// terminal scrollback or CI artefacts) defeats that.
    /// </summary>
    internal static string DescribeMcpServers(McpServerConfig[] configs)
    {
        var parts = new List<string>(configs.Length);
        foreach (var c in configs)
        {
            if (c.Type == "http" || c.Type == "sse")
                parts.Add($"{{name={c.Name},type={c.Type},url={c.Url},headers={c.Headers?.Length ?? 0}}}");
            else
                parts.Add($"{{name={c.Name},command={c.Command},args=[{string.Join(',', c.Args ?? [])}]}}");
        }
        return "[" + string.Join(", ", parts) + "]";
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
