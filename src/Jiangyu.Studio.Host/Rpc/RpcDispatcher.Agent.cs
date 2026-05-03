using System.Text.Json;
using InfiniFrame;
using Jiangyu.Acp.Schema;
using Jiangyu.Shared;
using Jiangyu.Studio.Host.Acp;

namespace Jiangyu.Studio.Host.Rpc;

public static partial class RpcDispatcher
{
    private static AgentProcessManager? _agentManager;
    private static AcpClientHandler? _agentHandler;

    /// <summary>
    /// Bumped on every <see cref="HandleAgentStart"/>. The async start task
    /// captures its generation when it begins and only commits its handler
    /// and manager to the statics if the generation is still current. If
    /// the user clicked another agent in the meantime, the started agent
    /// is silently torn down so we don't leak the subprocess or clobber
    /// the newer click's already-running agent.
    /// </summary>
    private static int _agentStartGeneration;

    // The official ACP registry. A single JSON document listing all published
    // agents with distribution configs. Fetched on demand when the registry
    // modal opens; no persistent cache.
    private const string AgentRegistryUrl =
        "https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json";

    private static readonly HttpClient AgentRegistryHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    static partial void RegisterAgentHandlers()
    {
        Register("agentStart", HandleAgentStart);
        Register("agentStop", HandleAgentStop);
        Register("agentSessionCreate", HandleAgentSessionCreate);
        Register("agentSessionPrompt", HandleAgentSessionPrompt);
        Register("agentSessionCancel", HandleAgentSessionCancel);
        Register("agentSessionClose", HandleAgentSessionClose);
        Register("agentPermissionResponse", HandleAgentPermissionResponse);
        Register("agentsRegistryFetch", HandleAgentsRegistryFetch);
    }

    /// <summary>
    /// Fires off the registry fetch and returns immediately. The result
    /// arrives as an <c>agentsRegistryFetched</c> notification carrying
    /// either <c>registry</c> (the raw JSON document) or <c>error</c>.
    /// Off-thread so the WebView dispatch doesn't block on network IO.
    /// </summary>
    private static JsonElement HandleAgentsRegistryFetch(IInfiniFrameWindow window, JsonElement? __)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var response = await AgentRegistryHttp
                    .GetAsync(AgentRegistryUrl)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                using var doc = JsonDocument.Parse(body);
                SendNotification(window, "agentsRegistryFetched", new
                {
                    registry = doc.RootElement.Clone(),
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Agent] Registry fetch failed: {ex}");
                SendNotification(window, "agentsRegistryFetched", new
                {
                    error = ex.Message,
                });
            }
        });

        return JsonSerializer.SerializeToElement(new { accepted = true });
    }

    private static JsonElement HandleAgentStart(IInfiniFrameWindow window, JsonElement? parameters)
    {
        var command = RequireString(parameters, "command");
        var argsEl = parameters?.GetProperty("args");
        var args = argsEl is { ValueKind: JsonValueKind.Array }
            ? argsEl.Value.EnumerateArray().Select(a => a.GetString()!).ToArray()
            : Array.Empty<string>();

        // Tear down any existing agent. Capture the old refs and null the
        // statics first so a session_update racing through the listen loop
        // can't forward to a half-disposed handler. ReleaseAllTerminals runs
        // on the captured handler so child processes die before we drop the
        // last reference.
        var oldHandler = _agentHandler;
        var oldManager = _agentManager;
        _agentHandler = null;
        _agentManager = null;
        oldHandler?.ReleaseAllTerminals();
        oldManager?.Stop();

        // Bump the generation BEFORE kicking off Task.Run so any in-flight
        // start task launched by a previous click sees that it's been
        // superseded and abandons its commit. Without this, a fast user
        // double-click leaks a bunx subprocess and lets the older start's
        // post-await assignment clobber the newer one.
        var generation = Interlocked.Increment(ref _agentStartGeneration);

        var handler = new AcpClientHandler(window);
        var manager = new AgentProcessManager();
        // McpPort is set during startup (see Program.cs).
        manager.McpPort = McpPort;
        manager.OnProcessExited = () =>
        {
            // Only reflect the exit in UI state if THIS manager is still the
            // committed one. Otherwise we either (a) tore down ourselves to
            // make way for a newer start, or (b) we're an abandoned start
            // whose subprocess died after Stop() ran. Either way, the user
            // is now connected to a different (or no) agent and shouldn't
            // see a `running: false` flicker.
            if (_agentManager == manager)
            {
                _agentManager = null;
                _agentHandler = null;
                SendNotification(window, "agentStatus", new { running = false });
            }
        };

        // Run async so we don't block the InfiniFrame message loop while the
        // agent subprocess starts (bunx can take several seconds to resolve).
        _ = Task.Run(async () =>
        {
            try
            {
                Console.Error.WriteLine($"[Agent] Starting: {command} {string.Join(' ', args)}");
                var initResponse = await manager.StartAsync(command, args, handler).ConfigureAwait(false);
                Console.Error.WriteLine($"[Agent] Initialised: {initResponse.AgentInfo?.Name} v{initResponse.AgentInfo?.Version}");

                if (Volatile.Read(ref _agentStartGeneration) != generation)
                {
                    // Superseded by a later HandleAgentStart. Drop this one
                    // on the floor: tear down terminals, stop the subprocess,
                    // and stay quiet so the frontend's view of the newer
                    // agent isn't disturbed.
                    Console.Error.WriteLine("[Agent] Start superseded; tearing down");
                    handler.ReleaseAllTerminals();
                    manager.Stop();
                    return;
                }

                _agentManager = manager;
                _agentHandler = handler;

                SendNotification(window, "agentStatus", new { running = true });
                SendNotification(window, "agentStartResult", new
                {
                    agentName = initResponse.AgentInfo?.Name,
                    agentVersion = initResponse.AgentInfo?.Version,
                    protocolVersion = initResponse.ProtocolVersion,
                    authMethods = initResponse.AuthMethods,
                });
                Console.Error.WriteLine("[Agent] agentStartResult notification sent");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Agent] Start failed: {ex}");
                manager.Stop();
                // Don't surface the failure to the frontend if we've been
                // superseded — the frontend already moved on to a different
                // agent and this error belongs to a forgotten attempt.
                if (Volatile.Read(ref _agentStartGeneration) == generation)
                {
                    SendNotification(window, "agentStartResult", new
                    {
                        error = ex.Message,
                    });
                }
            }
        });

        return JsonSerializer.SerializeToElement(new { accepted = true });
    }

    private static JsonElement HandleAgentStop(IInfiniFrameWindow window, JsonElement? _)
    {
        // Bump the generation first so any HandleAgentStart whose Task.Run
        // is still awaiting StartAsync sees it's been superseded and tears
        // down on completion instead of overwriting the (now null) statics.
        Interlocked.Increment(ref _agentStartGeneration);

        // Same pattern: null the statics before disposing the captured copies.
        var handler = _agentHandler;
        var manager = _agentManager;
        _agentHandler = null;
        _agentManager = null;
        handler?.ReleaseAllTerminals();
        manager?.Stop();

        SendNotification(window, "agentStatus", new { running = false });
        return NullElement;
    }

    private static JsonElement HandleAgentSessionCreate(IInfiniFrameWindow window, JsonElement? __)
    {
        var manager = _agentManager ?? throw new InvalidOperationException("Agent not running.");
        var projectRoot = ProjectWatcher.ProjectRoot
            ?? throw new InvalidOperationException("No project open.");

        _ = Task.Run(async () =>
        {
            try
            {
                var response = await manager.NewSessionAsync(projectRoot).ConfigureAwait(false);
                SendNotification(window, "agentSessionCreated", new
                {
                    sessionId = response.SessionId,
                });
            }
            catch (Exception ex)
            {
                SendNotification(window, "agentSessionCreated", new
                {
                    error = ex.Message,
                });
            }
        });

        return JsonSerializer.SerializeToElement(new { accepted = true });
    }

    /// <summary>
    /// Fires off the prompt and returns immediately. The final
    /// <c>stopReason</c> arrives as an <c>agentPromptResult</c> notification
    /// when the turn ends, so the WebView dispatch thread isn't blocked for
    /// the lifetime of the turn (which can be minutes). This is what makes
    /// concurrent <c>agentSessionCancel</c> RPCs reachable while a prompt is
    /// in flight.
    /// </summary>
    private static JsonElement HandleAgentSessionPrompt(IInfiniFrameWindow window, JsonElement? parameters)
    {
        var text = RequireString(parameters, "text");
        var manager = _agentManager ?? throw new InvalidOperationException("Agent not running.");

        _ = Task.Run(async () =>
        {
            string stopReason;
            string? error = null;
            try
            {
                var response = await manager.PromptAsync(text).ConfigureAwait(false);
                stopReason = response.StopReason;
            }
            catch (Exception ex)
            {
                stopReason = "error";
                error = ex.Message;
            }

            SendNotification(window, "agentPromptResult", new
            {
                stopReason,
                error,
            });
        });

        return JsonSerializer.SerializeToElement(new { accepted = true });
    }

    private static JsonElement HandleAgentSessionCancel(IInfiniFrameWindow _, JsonElement? __)
    {
        _agentManager?.CancelAsync().GetAwaiter().GetResult();
        return NullElement;
    }

    private static JsonElement HandleAgentSessionClose(IInfiniFrameWindow _, JsonElement? __)
    {
        _agentManager?.CloseSessionAsync().GetAwaiter().GetResult();
        return NullElement;
    }

    private static JsonElement HandleAgentPermissionResponse(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var permissionId = RequireString(parameters, "permissionId");
        var outcomeStr = RequireString(parameters, "outcome");

        PermissionOutcome outcome = outcomeStr switch
        {
            "selected" => new SelectedPermissionOutcome
            {
                OptionId = RequireString(parameters, "optionId"),
            },
            "cancelled" => new CancelledPermissionOutcome(),
            _ => throw new ArgumentException($"Unknown permission outcome: {outcomeStr}"),
        };

        var handler = _agentHandler ?? throw new InvalidOperationException("Agent not running.");
        handler.ResolvePermission(permissionId, outcome);

        return NullElement;
    }

    /// <summary>
    /// The MCP server port, set from Program.cs so agent sessions can pass
    /// it in their MCP server config.
    /// </summary>
    internal static int McpPort { get; set; }
}
