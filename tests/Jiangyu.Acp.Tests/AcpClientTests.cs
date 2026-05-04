using System.Text;
using System.Text.Json;
using Jiangyu.Acp.Schema;

namespace Jiangyu.Acp.Tests;

public class AcpClientTests
{
    [Fact]
    public async Task InitializeAsync_SendsAndReceives()
    {
        using var clientToAgent = new BlockingStream();
        using var agentToClient = new BlockingStream();

        // Mock agent: reads one request, sends back a response.
        var agentTask = Task.Run(async () =>
        {
            // Read the request.
            var (id, method) = await ReadJsonRpcRequestAsync(clientToAgent);
            Assert.Equal("initialize", method);

            // Send response.
            var result = JsonSerializer.Serialize(new
            {
                protocolVersion = 1,
                agentInfo = new { name = "test-agent", version = "1.0" },
            });
            await WriteJsonRpcResponseAsync(agentToClient, id, result);
        });

        var handler = new StubClientHandler();
        using var client = new AcpClient(handler, agentToClient, clientToAgent);
        client.Start();

        var response = await client.InitializeAsync(new InitializeRequest
        {
            ProtocolVersion = 1,
            ClientCapabilities = new ClientCapabilities(),
            ClientInfo = new Implementation { Name = "test", Version = "0.0.0" },
        });

        Assert.Equal(1, response.ProtocolVersion);
        Assert.Equal("test-agent", response.AgentInfo!.Name);

        await agentTask;
    }

    [Fact]
    public async Task SessionUpdate_NotificationForwardedToHandler()
    {
        using var clientToAgent = new BlockingStream();
        using var agentToClient = new BlockingStream();

        var handler = new StubClientHandler();
        using var client = new AcpClient(handler, agentToClient, clientToAgent);
        client.Start();

        // Agent sends a session/update notification.
        var updateJson = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "session/update",
            @params = new
            {
                sessionId = "s-1",
                update = new
                {
                    sessionUpdate = "agent_message_chunk",
                    content = new { type = "text", text = "thinking..." },
                },
            },
        });
        await WriteFramedAsync(agentToClient, updateJson);

        // Give the listen loop time to process.
        await Task.Delay(200);

        Assert.Single(handler.ReceivedUpdates);
        Assert.Equal("s-1", handler.ReceivedUpdates[0].SessionId);
        var chunk = Assert.IsType<AgentMessageChunkUpdate>(handler.ReceivedUpdates[0].Update);
        var text = Assert.IsType<TextContentBlock>(chunk.Content);
        Assert.Equal("thinking...", text.Text);
    }

    [Fact]
    public async Task AuthenticateAsync_SendsMethodIdAndAcceptsEmptyResult()
    {
        // ACP authenticate is request/response with the method id in the
        // params. Copilot returns an empty object on success.
        using var clientToAgent = new BlockingStream();
        using var agentToClient = new BlockingStream();

        var agentTask = Task.Run(async () =>
        {
            var json = await ReadFramedAsync(clientToAgent);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal("authenticate", root.GetProperty("method").GetString());
            Assert.Equal("github", root.GetProperty("params").GetProperty("methodId").GetString());

            await WriteJsonRpcResponseAsync(agentToClient, root.GetProperty("id").GetInt32(), "{}");
        });

        var handler = new StubClientHandler();
        using var client = new AcpClient(handler, agentToClient, clientToAgent);
        client.Start();

        var response = await client.AuthenticateAsync(new AuthenticateRequest { MethodId = "github" });
        Assert.NotNull(response);

        await agentTask;
    }

    [Fact]
    public async Task AuthenticateAsync_PropagatesAuthRequiredErrorCode()
    {
        // ACP signals "you must sign in before session/new" via JSON-RPC
        // -32000. The client must surface the code on AcpException so the
        // host's auth_required catch can branch on it.
        using var clientToAgent = new BlockingStream();
        using var agentToClient = new BlockingStream();

        var agentTask = Task.Run(async () =>
        {
            var json = await ReadFramedAsync(clientToAgent);
            var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.GetProperty("id").GetInt32();

            var errorJson =
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-32000,\"message\":\"auth_required\"}}}}";
            await WriteFramedAsync(agentToClient, errorJson);
        });

        var handler = new StubClientHandler();
        using var client = new AcpClient(handler, agentToClient, clientToAgent);
        client.Start();

        var ex = await Assert.ThrowsAsync<AcpException>(() =>
            client.NewSessionAsync(new NewSessionRequest
            {
                Cwd = "/tmp",
                McpServers = [],
            }));
        Assert.Equal(-32000, ex.ErrorCode);
        Assert.Equal("auth_required", ex.Message);

        await agentTask;
    }

    [Fact]
    public async Task ReadTextFile_AgentRequestDelegatedToHandler()
    {
        using var clientToAgent = new BlockingStream();
        using var agentToClient = new BlockingStream();

        var handler = new StubClientHandler
        {
            ReadTextFileResult = new ReadTextFileResponse { Content = "file content" },
        };

        using var client = new AcpClient(handler, agentToClient, clientToAgent);
        client.Start();

        // Agent sends an fs/read_text_file request.
        var requestJson = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 99,
            method = "fs/read_text_file",
            @params = new { sessionId = "s-1", path = "/src/test.cs" },
        });
        await WriteFramedAsync(agentToClient, requestJson);

        // Read the response.
        var (responseId, result, _) = await ReadJsonRpcResponseAsync(clientToAgent);

        Assert.Equal(99, responseId);
        Assert.Contains("file content", result);
    }

    // --- Helpers ---

    internal static async Task<(int id, string method)> ReadJsonRpcRequestAsync(Stream stream)
    {
        var json = await ReadFramedAsync(stream);
        var doc = JsonDocument.Parse(json);
        return (doc.RootElement.GetProperty("id").GetInt32(), doc.RootElement.GetProperty("method").GetString()!);
    }

    internal static Task WriteJsonRpcResponseAsync(Stream stream, int id, string resultJson)
        => WriteFramedAsync(stream, $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{resultJson}}}");

    internal static async Task<(int? id, string result, string? error)> ReadJsonRpcResponseAsync(Stream stream)
    {
        var json = await ReadFramedAsync(stream);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        int? id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : null;
        var result = root.TryGetProperty("result", out var resultEl) ? resultEl.GetRawText() : "";
        var error = root.TryGetProperty("error", out var errorEl) ? errorEl.GetRawText() : null;
        return (id, result, error);
    }

    internal static async Task WriteFramedAsync(Stream stream, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    internal static async Task<string> ReadFramedAsync(Stream stream)
    {
        int contentLength = -1;
        var headerBuf = new List<byte>();
        var one = new byte[1];

        while (true)
        {
            if (await stream.ReadAsync(one) == 0) throw new EndOfStreamException();
            headerBuf.Add(one[0]);

            // Look for end of header line (\r\n) or end of headers (\r\n\r\n).
            if (headerBuf.Count >= 4 &&
                headerBuf[^4] == (byte)'\r' && headerBuf[^3] == (byte)'\n' &&
                headerBuf[^2] == (byte)'\r' && headerBuf[^1] == (byte)'\n')
            {
                var headers = Encoding.ASCII.GetString(headerBuf.ToArray());
                foreach (var line in headers.Split("\r\n"))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line.AsSpan()["Content-Length:".Length..].Trim());
                }
                break;
            }
        }

        if (contentLength < 0) throw new InvalidOperationException("Missing Content-Length");

        var body = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = await stream.ReadAsync(body.AsMemory(read, contentLength - read));
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
        return Encoding.UTF8.GetString(body);
    }
}

/// <summary>
/// Stub handler that records calls for assertions.
/// </summary>
internal sealed class StubClientHandler : IAcpClientHandler
{
    public List<SessionNotification> ReceivedUpdates { get; } = [];

    public ReadTextFileResponse ReadTextFileResult { get; set; } = new() { Content = "" };
    public WriteTextFileResponse WriteTextFileResult { get; set; } = new();
    public RequestPermissionResponse PermissionResult { get; set; } = new() { Outcome = new SelectedPermissionOutcome { OptionId = "allow_once" } };

    public ValueTask<ReadTextFileResponse> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken ct)
        => ValueTask.FromResult(ReadTextFileResult);

    public ValueTask<WriteTextFileResponse> WriteTextFileAsync(WriteTextFileRequest request, CancellationToken ct)
        => ValueTask.FromResult(WriteTextFileResult);

    public ValueTask<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken ct)
        => ValueTask.FromResult(PermissionResult);

    public ValueTask<CreateTerminalResponse> CreateTerminalAsync(CreateTerminalRequest request, CancellationToken ct)
        => ValueTask.FromResult(new CreateTerminalResponse { TerminalId = "stub" });

    public ValueTask<TerminalOutputResponse> TerminalOutputAsync(TerminalOutputRequest request, CancellationToken ct)
        => ValueTask.FromResult(new TerminalOutputResponse { Output = "" });

    public ValueTask<WaitForTerminalExitResponse> WaitForTerminalExitAsync(WaitForTerminalExitRequest request, CancellationToken ct)
        => ValueTask.FromResult(new WaitForTerminalExitResponse { ExitCode = 0 });

    public ValueTask<KillTerminalResponse> KillTerminalAsync(KillTerminalRequest request, CancellationToken ct)
        => ValueTask.FromResult(new KillTerminalResponse());

    public ValueTask<ReleaseTerminalResponse> ReleaseTerminalAsync(ReleaseTerminalRequest request, CancellationToken ct)
        => ValueTask.FromResult(new ReleaseTerminalResponse());

    public ValueTask OnSessionUpdateAsync(SessionNotification notification, CancellationToken ct)
    {
        ReceivedUpdates.Add(notification);
        return ValueTask.CompletedTask;
    }
}
