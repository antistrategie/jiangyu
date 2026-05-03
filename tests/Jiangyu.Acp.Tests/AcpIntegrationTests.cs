using System.Text.Json;
using Jiangyu.Acp;
using Jiangyu.Acp.Schema;

namespace Jiangyu.Acp.Tests;

/// <summary>
/// Integration test that verifies the full ACP flow: client ↔ agent over
/// connected streams, including initialise, session/new, session/update
/// notification, and prompt round-trip.
/// </summary>
public class AcpIntegrationTests
{
    [Fact]
    public async Task FullFlow_Initialise_Session_Prompt()
    {
        // Wire up two pairs of streams to simulate stdio.
        using var clientToAgent = new BlockingStream();
        using var agentToClient = new BlockingStream();

        var handler = new StubClientHandler();
        using var client = new AcpClient(handler, agentToClient, clientToAgent);
        client.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Mock agent loop in background.
        var agentTask = Task.Run(async () =>
        {
            // 1. Respond to initialise.
            var (initId, initMethod) = await ReadRequestAsync(clientToAgent);
            Assert.Equal("initialize", initMethod);
            await WriteResponseAsync(agentToClient, initId, new
            {
                protocolVersion = 1,
                agentInfo = new { name = "mock-agent", version = "0.1.0" },
                agentCapabilities = new { loadSession = false },
            });

            // 2. Respond to session/new.
            var (sessionId, sessionMethod) = await ReadRequestAsync(clientToAgent);
            Assert.Equal("session/new", sessionMethod);
            await WriteResponseAsync(agentToClient, sessionId, new
            {
                sessionId = "sess-001",
            });

            // 3. Send a session/update notification (agent message chunk).
            await WriteNotificationAsync(agentToClient, "session/update", new
            {
                sessionId = "sess-001",
                update = new
                {
                    sessionUpdate = "agent_message_chunk",
                    content = new { type = "text", text = "I am thinking..." },
                },
            });

            // Small delay so the client processes the notification.
            await Task.Delay(100);

            // 4. Respond to session/prompt.
            var (promptId, promptMethod) = await ReadRequestAsync(clientToAgent);
            Assert.Equal("session/prompt", promptMethod);

            // Send another update before responding.
            await WriteNotificationAsync(agentToClient, "session/update", new
            {
                sessionId = "sess-001",
                update = new
                {
                    sessionUpdate = "agent_message_chunk",
                    content = new { type = "text", text = "Here is the answer." },
                },
            });

            await Task.Delay(50);

            await WriteResponseAsync(agentToClient, promptId, new
            {
                stopReason = "end_turn",
            });
        }, cts.Token);

        // --- Client side ---

        // 1. Initialise.
        var initResponse = await client.InitializeAsync(new InitializeRequest
        {
            ProtocolVersion = 1,
            ClientCapabilities = new ClientCapabilities
            {
                Fs = new FileSystemCapability { ReadTextFile = true, WriteTextFile = true },
                Terminal = true,
            },
            ClientInfo = new Implementation { Name = "test-client", Version = "0.0.0" },
        }, cts.Token);

        Assert.Equal(1, initResponse.ProtocolVersion);
        Assert.Equal("mock-agent", initResponse.AgentInfo!.Name);

        // 2. Create session.
        var sessionResponse = await client.NewSessionAsync(new NewSessionRequest
        {
            Cwd = "/tmp/test-project",
            McpServers =
            [
                new McpServerConfig { Name = "jiangyu", Type = "http", Url = "http://127.0.0.1:41697/mcp", Headers = [] },
            ],
        }, cts.Token);

        Assert.Equal("sess-001", sessionResponse.SessionId);

        // 3. Send prompt (agent will send updates + respond).
        var promptResponse = await client.PromptAsync(new PromptRequest
        {
            SessionId = "sess-001",
            Prompt = [new TextContentBlock { Text = "Help me create a mod" }],
        }, cts.Token);

        Assert.Equal("end_turn", promptResponse.StopReason);

        // 4. Verify we received the session updates.
        // Give a moment for any remaining notifications.
        await Task.Delay(100);
        Assert.True(handler.ReceivedUpdates.Count >= 2, $"Expected at least 2 updates, got {handler.ReceivedUpdates.Count}");

        var first = Assert.IsType<AgentMessageChunkUpdate>(handler.ReceivedUpdates[0].Update);
        Assert.Equal("I am thinking...", ((TextContentBlock)first.Content).Text);

        var second = Assert.IsType<AgentMessageChunkUpdate>(handler.ReceivedUpdates[1].Update);
        Assert.Equal("Here is the answer.", ((TextContentBlock)second.Content).Text);

        await agentTask;
    }

    [Fact]
    public async Task AgentRequestsReadFile_ClientResponds()
    {
        using var clientToAgent = new BlockingStream();
        using var agentToClient = new BlockingStream();

        var handler = new StubClientHandler
        {
            ReadTextFileResult = new ReadTextFileResponse { Content = "fn main() {}" },
        };

        using var client = new AcpClient(handler, agentToClient, clientToAgent);
        client.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Agent sends an fs/read_text_file request to the client.
        var agentTask = Task.Run(async () =>
        {
            var requestJson = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 42,
                method = "fs/read_text_file",
                @params = new { sessionId = "s-1", path = "/src/main.rs" },
            });
            await WriteFramedAsync(agentToClient, requestJson);

            // Read the client's response.
            var responseJson = await ReadFramedAsync(clientToAgent);
            var doc = JsonDocument.Parse(responseJson);
            Assert.Equal(42, doc.RootElement.GetProperty("id").GetInt32());
            var content = doc.RootElement.GetProperty("result").GetProperty("content").GetString();
            Assert.Equal("fn main() {}", content);
        }, cts.Token);

        await agentTask;
    }

    [Fact]
    public async Task AgentRequestsPermission_ClientResolvesIt()
    {
        using var clientToAgent = new BlockingStream();
        using var agentToClient = new BlockingStream();

        var handler = new StubClientHandler
        {
            PermissionResult = new RequestPermissionResponse
            {
                Outcome = new SelectedPermissionOutcome { OptionId = "allow_once" },
            },
        };

        using var client = new AcpClient(handler, agentToClient, clientToAgent);
        client.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var agentTask = Task.Run(async () =>
        {
            var requestJson = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 7,
                method = "session/request_permission",
                @params = new
                {
                    sessionId = "s-1",
                    toolCall = new { toolCallId = "tc-1", title = "Write to /src/file.cs" },
                    options = new object[]
                    {
                        new { optionId = "allow_once", name = "Allow", kind = "allow_once" },
                        new { optionId = "reject_once", name = "Deny", kind = "reject_once" },
                    },
                },
            });
            await WriteFramedAsync(agentToClient, requestJson);

            var responseJson = await ReadFramedAsync(clientToAgent);
            var doc = JsonDocument.Parse(responseJson);
            Assert.Equal(7, doc.RootElement.GetProperty("id").GetInt32());
            var outcome = doc.RootElement.GetProperty("result").GetProperty("outcome");
            Assert.Equal("selected", outcome.GetProperty("outcome").GetString());
            Assert.Equal("allow_once", outcome.GetProperty("optionId").GetString());
        }, cts.Token);

        await agentTask;
    }

    // --- Helpers ---

    private static Task<(int id, string method)> ReadRequestAsync(Stream stream)
        => AcpClientTests.ReadJsonRpcRequestAsync(stream);

    private static Task WriteResponseAsync(Stream stream, int id, object result)
        => AcpClientTests.WriteFramedAsync(stream, JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            result,
        }));

    private static Task WriteNotificationAsync(Stream stream, string method, object @params)
        => AcpClientTests.WriteFramedAsync(stream, JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method,
            @params,
        }));

    private static Task WriteFramedAsync(Stream stream, string json)
        => AcpClientTests.WriteFramedAsync(stream, json);

    private static Task<string> ReadFramedAsync(Stream stream)
        => AcpClientTests.ReadFramedAsync(stream);
}
