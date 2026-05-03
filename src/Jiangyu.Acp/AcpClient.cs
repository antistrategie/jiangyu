using System.Text.Json;
using Jiangyu.Acp.JsonRpc;
using Jiangyu.Acp.Schema;

namespace Jiangyu.Acp;

/// <summary>
/// ACP client-side connection. Wraps a JSON-RPC endpoint connected to an
/// agent subprocess via stdio. Sends requests to the agent and dispatches
/// agent-to-client callbacks through <see cref="IAcpClientHandler"/>.
/// </summary>
public sealed class AcpClient : IDisposable
{
    private readonly JsonRpcEndpoint _endpoint;
    private readonly IAcpClientHandler _handler;
    private readonly CancellationTokenSource _cts = new();

    public AcpClient(IAcpClientHandler handler, Stream input, Stream output, FramingMode framing = FramingMode.ContentLength)
    {
        _handler = handler;
        _endpoint = new JsonRpcEndpoint(input, output, framing);

        _endpoint.OnRequest(HandleAgentRequestAsync);
        _endpoint.OnNotification(HandleAgentNotificationAsync);
    }

    /// <summary>
    /// Starts the background read loop. Call once after construction.
    /// </summary>
    public void Start()
    {
        _ = Task.Run(() => _endpoint.ListenAsync(_cts.Token));
    }

    public async Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken ct = default)
    {
        return await SendAsync<InitializeRequest, InitializeResponse>("initialize", request, ct).ConfigureAwait(false);
    }

    public async Task<AuthenticateResponse> AuthenticateAsync(AuthenticateRequest request, CancellationToken ct = default)
    {
        return await SendAsync<AuthenticateRequest, AuthenticateResponse>("authenticate", request, ct).ConfigureAwait(false);
    }

    public async Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken ct = default)
    {
        return await SendAsync<NewSessionRequest, NewSessionResponse>("session/new", request, ct).ConfigureAwait(false);
    }

    public async Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken ct = default)
    {
        return await SendAsync<PromptRequest, PromptResponse>("session/prompt", request, ct).ConfigureAwait(false);
    }

    public async Task CancelAsync(string sessionId, CancellationToken ct = default)
    {
        var notification = new CancelNotification { SessionId = sessionId };
        var @params = JsonSerializer.SerializeToElement(notification);
        await _endpoint.SendNotificationAsync("session/cancel", @params, ct).ConfigureAwait(false);
    }

    public async Task<LoadSessionResponse> LoadSessionAsync(LoadSessionRequest request, CancellationToken ct = default)
    {
        return await SendAsync<LoadSessionRequest, LoadSessionResponse>("session/load", request, ct).ConfigureAwait(false);
    }

    public async Task<CloseSessionResponse> CloseSessionAsync(CloseSessionRequest request, CancellationToken ct = default)
    {
        return await SendAsync<CloseSessionRequest, CloseSessionResponse>("session/close", request, ct).ConfigureAwait(false);
    }

    public async Task<ListSessionsResponse> ListSessionsAsync(ListSessionsRequest? request = null, CancellationToken ct = default)
    {
        return await SendAsync<ListSessionsRequest, ListSessionsResponse>(
            "session/list", request ?? new ListSessionsRequest(), ct).ConfigureAwait(false);
    }

    public async Task<SetConfigOptionResponse> SetConfigOptionAsync(SetConfigOptionRequest request, CancellationToken ct = default)
    {
        return await SendAsync<SetConfigOptionRequest, SetConfigOptionResponse>("session/set_config_option", request, ct).ConfigureAwait(false);
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken ct)
    {
        var @params = JsonSerializer.SerializeToElement(request);
        var response = await _endpoint.SendRequestAsync(method, @params, ct).ConfigureAwait(false);

        if (response.Error is not null)
            throw new AcpException(response.Error.Message, response.Error.Code);

        if (response.Result is null)
            return default!;

        return JsonSerializer.Deserialize<TResponse>(response.Result.Value)!;
    }

    private async ValueTask<JsonElement?> HandleAgentRequestAsync(string method, JsonElement? @params, CancellationToken ct)
    {
        return method switch
        {
            "fs/read_text_file" => JsonSerializer.SerializeToElement(
                await _handler.ReadTextFileAsync(Deserialize<ReadTextFileRequest>(@params), ct)),

            "fs/write_text_file" => JsonSerializer.SerializeToElement(
                await _handler.WriteTextFileAsync(Deserialize<WriteTextFileRequest>(@params), ct)),

            "session/request_permission" => JsonSerializer.SerializeToElement(
                await _handler.RequestPermissionAsync(Deserialize<RequestPermissionRequest>(@params), ct)),

            "terminal/create" => JsonSerializer.SerializeToElement(
                await _handler.CreateTerminalAsync(Deserialize<CreateTerminalRequest>(@params), ct)),

            "terminal/output" => JsonSerializer.SerializeToElement(
                await _handler.TerminalOutputAsync(Deserialize<TerminalOutputRequest>(@params), ct)),

            "terminal/wait_for_exit" => JsonSerializer.SerializeToElement(
                await _handler.WaitForTerminalExitAsync(Deserialize<WaitForTerminalExitRequest>(@params), ct)),

            "terminal/kill" => JsonSerializer.SerializeToElement(
                await _handler.KillTerminalAsync(Deserialize<KillTerminalRequest>(@params), ct)),

            "terminal/release" => JsonSerializer.SerializeToElement(
                await _handler.ReleaseTerminalAsync(Deserialize<ReleaseTerminalRequest>(@params), ct)),

            _ => throw new AcpException($"Unknown method: {method}", JsonRpcErrorCodes.MethodNotFound),
        };
    }

    private async ValueTask HandleAgentNotificationAsync(string method, JsonElement? @params, CancellationToken ct)
    {
        if (method == "session/update" && @params is not null)
        {
            try
            {
                var notification = JsonSerializer.Deserialize<SessionNotification>(@params.Value)!;
                await _handler.OnSessionUpdateAsync(notification, ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"[Acp] Failed to deserialise session/update: {ex.Message}");
            }
        }
    }

    private static T Deserialize<T>(JsonElement? element)
    {
        if (element is null)
            throw new AcpException("Missing params.", JsonRpcErrorCodes.InvalidParams);

        return JsonSerializer.Deserialize<T>(element.Value)
            ?? throw new AcpException("Failed to deserialise params.", JsonRpcErrorCodes.InvalidParams);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _endpoint.Dispose();
        _cts.Dispose();
    }
}
