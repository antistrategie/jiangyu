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

    static partial void RegisterAgentHandlers()
    {
        Register("agentStart", HandleAgentStart);
        Register("agentStop", HandleAgentStop);
        Register("agentSessionCreate", HandleAgentSessionCreate);
        Register("agentSessionPrompt", HandleAgentSessionPrompt);
        Register("agentSessionCancel", HandleAgentSessionCancel);
        Register("agentSessionClose", HandleAgentSessionClose);
        Register("agentPermissionResponse", HandleAgentPermissionResponse);
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

        var handler = new AcpClientHandler(window);
        var manager = new AgentProcessManager();
        // McpPort is set during startup (see Program.cs).
        manager.McpPort = McpPort;
        manager.OnProcessExited = () =>
        {
            // Clear statics so subsequent calls don't use a dead manager.
            if (_agentManager == manager)
            {
                _agentManager = null;
                _agentHandler = null;
            }
            SendNotification(window, "agentStatus", new { running = false });
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
                SendNotification(window, "agentStartResult", new
                {
                    error = ex.Message,
                });
            }
        });

        return JsonSerializer.SerializeToElement(new { accepted = true });
    }

    private static JsonElement HandleAgentStop(IInfiniFrameWindow window, JsonElement? _)
    {
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
