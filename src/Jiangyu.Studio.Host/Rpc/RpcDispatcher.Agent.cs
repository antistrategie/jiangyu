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

        // Tear down any existing agent. ReleaseAllTerminals first so child
        // processes spawned by the previous agent are killed before we drop
        // the handler reference.
        _agentHandler?.ReleaseAllTerminals();
        _agentManager?.Stop();

        var handler = new AcpClientHandler(window);
        var manager = new AgentProcessManager();
        // McpPort is set during startup (see Program.cs).
        manager.McpPort = McpPort;

        var initResponse = manager.StartAsync(command, args, handler).GetAwaiter().GetResult();

        _agentManager = manager;
        _agentHandler = handler;

        SendNotification(window, "agentStatus", new { running = true });

        return JsonSerializer.SerializeToElement(new
        {
            agentName = initResponse.AgentInfo?.Name,
            agentVersion = initResponse.AgentInfo?.Version,
            protocolVersion = initResponse.ProtocolVersion,
            authMethods = initResponse.AuthMethods,
        });
    }

    private static JsonElement HandleAgentStop(IInfiniFrameWindow window, JsonElement? _)
    {
        _agentHandler?.ReleaseAllTerminals();
        _agentManager?.Stop();
        _agentManager = null;
        _agentHandler = null;

        SendNotification(window, "agentStatus", new { running = false });
        return NullElement;
    }

    private static JsonElement HandleAgentSessionCreate(IInfiniFrameWindow _, JsonElement? __)
    {
        var manager = _agentManager ?? throw new InvalidOperationException("Agent not running.");
        var projectRoot = ProjectWatcher.ProjectRoot
            ?? throw new InvalidOperationException("No project open.");

        var response = manager.NewSessionAsync(projectRoot).GetAwaiter().GetResult();

        return JsonSerializer.SerializeToElement(new
        {
            sessionId = response.SessionId,
            modes = response.Modes,
            models = response.Models,
        });
    }

    private static JsonElement HandleAgentSessionPrompt(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var text = RequireString(parameters, "text");
        var manager = _agentManager ?? throw new InvalidOperationException("Agent not running.");

        var response = manager.PromptAsync(text).GetAwaiter().GetResult();

        return JsonSerializer.SerializeToElement(new
        {
            stopReason = response.StopReason,
        });
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
            "allowed" => new AllowedPermissionOutcome
            {
                OptionLabel = TryGetString(parameters, "optionLabel"),
            },
            "denied" => new DeniedPermissionOutcome(),
            "dismissed" => new DismissedPermissionOutcome(),
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
