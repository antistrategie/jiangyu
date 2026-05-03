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
    /// The agent's self-reported identity from the last successful
    /// initialize handshake. Used to record agent name on new sessions
    /// and to gate resume on <c>agentCapabilities.loadSession</c>. Null
    /// when no agent is running.
    /// </summary>
    private static InitializeResponse? _agentStartLastResponse;

    /// <summary>
    /// The Jiangyu InstalledAgent.id we asked the host to start (e.g.
    /// "claude-acp"). Recorded in session metadata so the history popover
    /// can route a resume click to the right agent — distinct from
    /// <see cref="_agentStartLastResponse"/>.AgentInfo.Name, which is the
    /// agent's self-reported initialize name (often a package id).
    /// </summary>
    private static string? _currentAgentId;

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
        Register("agentSessionsList", HandleAgentSessionsList);
        Register("agentSessionDelete", HandleAgentSessionDelete);
        Register("agentSessionResume", HandleAgentSessionResume);
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
        var agentId = TryGetString(parameters, "agentId");

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
        // MCP runs over stdio now; no port to plumb.
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
                _agentStartLastResponse = null;
                _currentAgentId = null;
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
                _agentStartLastResponse = initResponse;
                _currentAgentId = agentId;

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
        _agentStartLastResponse = null;
        _currentAgentId = null;
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

        // The session metadata file is written lazily, on the first prompt
        // (see HandleAgentSessionPrompt). An empty "I just clicked the
        // agent panel and didn't say anything" session shouldn't clutter
        // the history popover.

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

        // Persist on the first prompt of a session (creates the entry with
        // agent identity + a preview of the first message); subsequent
        // prompts just bump updatedAt. The "first prompt" detection is
        // "session not yet in the store OR has no firstMessage" — this is
        // the lazy create that keeps empty never-prompted sessions out of
        // the history popover.
        var sessionId = manager.SessionId;
        var projectRoot = ProjectWatcher.ProjectRoot;
        if (sessionId is not null && projectRoot is not null)
        {
            try
            {
                var existing = AgentSessionsStore.Load(projectRoot)
                    .Sessions.FirstOrDefault(s => s.Id == sessionId);
                if (existing is null || existing.FirstMessage is null)
                {
                    var preview = text.Length > 200 ? text[..200] : text;
                    AgentSessionsStore.Upsert(
                        projectRoot,
                        sessionId,
                        agentId: _currentAgentId,
                        agentName: _agentStartLastResponse?.AgentInfo?.Name,
                        firstMessage: preview);
                }
                else
                {
                    AgentSessionsStore.Upsert(projectRoot, sessionId);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Sessions] Failed to bump session: {ex.Message}");
            }
        }

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

    private static JsonElement HandleAgentSessionsList(IInfiniFrameWindow _, JsonElement? __)
    {
        var projectRoot = ProjectWatcher.ProjectRoot
            ?? throw new InvalidOperationException("No project open.");
        var file = AgentSessionsStore.Load(projectRoot);
        return JsonSerializer.SerializeToElement(file);
    }

    private static JsonElement HandleAgentSessionDelete(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var sessionId = RequireString(parameters, "sessionId");
        var projectRoot = ProjectWatcher.ProjectRoot
            ?? throw new InvalidOperationException("No project open.");
        var file = AgentSessionsStore.Remove(projectRoot, sessionId);
        return JsonSerializer.SerializeToElement(file);
    }

    /// <summary>
    /// Resumes a previously-stored session by id. Returns immediately with
    /// an ack; success or failure arrives as <c>agentSessionResumed</c>
    /// notification once the agent has finished streaming historical
    /// session updates (which flow through the existing <c>agentUpdate</c>
    /// path during the await).
    /// </summary>
    private static JsonElement HandleAgentSessionResume(IInfiniFrameWindow window, JsonElement? parameters)
    {
        var sessionId = RequireString(parameters, "sessionId");
        var manager = _agentManager ?? throw new InvalidOperationException("Agent not running.");
        var projectRoot = ProjectWatcher.ProjectRoot
            ?? throw new InvalidOperationException("No project open.");

        _ = Task.Run(async () =>
        {
            try
            {
                await manager.LoadSessionAsync(sessionId, projectRoot).ConfigureAwait(false);
                AgentSessionsStore.Upsert(projectRoot, sessionId);
                SendNotification(window, "agentSessionResumed", new
                {
                    sessionId,
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Agent] Resume failed: {ex}");
                SendNotification(window, "agentSessionResumed", new
                {
                    error = ex.Message,
                });
            }
        });

        return JsonSerializer.SerializeToElement(new { accepted = true });
    }

}
