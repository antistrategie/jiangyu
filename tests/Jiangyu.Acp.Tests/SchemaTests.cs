using System.Text.Json;
using Jiangyu.Acp.Schema;

namespace Jiangyu.Acp.Tests;

public class SchemaTests
{
    [Fact]
    public void SessionUpdate_Deserialises_AgentMessageChunk()
    {
        var json = """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"hello"}}""";
        var update = JsonSerializer.Deserialize<SessionUpdate>(json);

        var chunk = Assert.IsType<AgentMessageChunkUpdate>(update);
        var text = Assert.IsType<TextContentBlock>(chunk.Content);
        Assert.Equal("hello", text.Text);
    }

    [Fact]
    public void SessionUpdate_Deserialises_ToolCall()
    {
        var json = """
        {
            "sessionUpdate": "tool_call",
            "toolCallId": "tc-1",
            "title": "Read file",
            "content": [{"type":"text","text":"reading..."}],
            "kind": "read",
            "status": "running"
        }
        """;
        var update = JsonSerializer.Deserialize<SessionUpdate>(json);

        var tc = Assert.IsType<ToolCallUpdate>(update);
        Assert.Equal("tc-1", tc.ToolCallId);
        Assert.Equal("Read file", tc.Title);
        Assert.Equal("read", tc.Kind);
        Assert.Equal("running", tc.Status);
        Assert.Single(tc.Content!);
    }

    [Fact]
    public void SessionUpdate_Deserialises_Plan()
    {
        var json = """
        {
            "sessionUpdate": "plan",
            "entries": [
                {"id":"e1","title":"Step 1","status":"completed","priority":1},
                {"id":"e2","title":"Step 2","status":"in_progress"}
            ]
        }
        """;
        var update = JsonSerializer.Deserialize<SessionUpdate>(json);

        var plan = Assert.IsType<PlanUpdate>(update);
        Assert.Equal(2, plan.Entries.Length);
        Assert.Equal("Step 1", plan.Entries[0].Title);
        Assert.Equal(1, plan.Entries[0].Priority);
    }

    [Fact]
    public void SessionUpdate_Deserialises_ToolCallUpdate()
    {
        var json = """
        {
            "sessionUpdate": "tool_call_update",
            "toolCallId": "tc-1",
            "content": [{"type":"diff","path":"/src/main.cs","diff":"@@ -1 +1 @@\n-old\n+new"}],
            "status": "completed"
        }
        """;
        var update = JsonSerializer.Deserialize<SessionUpdate>(json);

        var tcu = Assert.IsType<ToolCallProgressUpdate>(update);
        Assert.Equal("tc-1", tcu.ToolCallId);
        Assert.Equal("completed", tcu.Status);
        var diff = Assert.IsType<DiffToolCallContent>(tcu.Content![0]);
        Assert.Equal("/src/main.cs", diff.Path);
    }

    [Fact]
    public void SessionUpdate_Deserialises_CurrentModeUpdate()
    {
        var json = """{"sessionUpdate":"current_mode_update","currentModeId":"agent"}""";
        var update = JsonSerializer.Deserialize<SessionUpdate>(json);

        var mode = Assert.IsType<CurrentModeUpdate>(update);
        Assert.Equal("agent", mode.CurrentModeId);
    }

    [Fact]
    public void SessionUpdate_Deserialises_ConfigOptionUpdate()
    {
        var json = """{"sessionUpdate":"config_option_update","key":"model","value":"claude-4"}""";
        var update = JsonSerializer.Deserialize<SessionUpdate>(json);

        var cfg = Assert.IsType<ConfigOptionUpdate>(update);
        Assert.Equal("model", cfg.Key);
        Assert.Equal("claude-4", cfg.Value!.Value.GetString());
    }

    [Fact]
    public void SessionUpdate_Deserialises_SessionInfoUpdate()
    {
        var json = """{"sessionUpdate":"session_info_update","title":"My session"}""";
        var update = JsonSerializer.Deserialize<SessionUpdate>(json);

        var info = Assert.IsType<SessionInfoUpdate>(update);
        Assert.Equal("My session", info.Title);
    }

    [Fact]
    public void SessionUpdate_ThrowsOnUnknownType()
    {
        var json = """{"sessionUpdate":"unknown_type"}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SessionUpdate>(json));
    }

    [Fact]
    public void ContentBlock_Deserialises_Text()
    {
        var json = """{"type":"text","text":"hello"}""";
        var block = JsonSerializer.Deserialize<ContentBlock>(json);
        var text = Assert.IsType<TextContentBlock>(block);
        Assert.Equal("hello", text.Text);
    }

    [Fact]
    public void ContentBlock_Deserialises_Image()
    {
        var json = """{"type":"image","data":"base64data","mimeType":"image/png"}""";
        var block = JsonSerializer.Deserialize<ContentBlock>(json);
        var img = Assert.IsType<ImageContentBlock>(block);
        Assert.Equal("base64data", img.Data);
        Assert.Equal("image/png", img.MimeType);
    }

    [Fact]
    public void PermissionOutcome_RoundTrips()
    {
        var allowed = new AllowedPermissionOutcome { OptionLabel = "Always allow" };
        var json = JsonSerializer.Serialize<PermissionOutcome>(allowed);
        var rt = JsonSerializer.Deserialize<PermissionOutcome>(json);
        var result = Assert.IsType<AllowedPermissionOutcome>(rt);
        Assert.Equal("Always allow", result.OptionLabel);
    }

    [Fact]
    public void ToolCallContent_Deserialises_Terminal()
    {
        var json = """{"type":"terminal","terminalId":"t-1"}""";
        var content = JsonSerializer.Deserialize<ToolCallContent>(json);
        var terminal = Assert.IsType<TerminalToolCallContent>(content);
        Assert.Equal("t-1", terminal.TerminalId);
    }

    [Fact]
    public void InitializeRequest_Serialises()
    {
        var request = new InitializeRequest
        {
            ProtocolVersion = 1,
            ClientCapabilities = new ClientCapabilities
            {
                Fs = new FileSystemCapability { ReadTextFile = true, WriteTextFile = true },
                Terminal = true,
            },
            ClientInfo = new Implementation
            {
                Name = "jiangyu-studio",
                Title = "Jiangyu Studio",
                Version = "0.1.0",
            },
        };

        var json = JsonSerializer.Serialize(request);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("protocolVersion").GetInt32());
        Assert.True(root.GetProperty("clientCapabilities").GetProperty("fs").GetProperty("readTextFile").GetBoolean());
        Assert.True(root.GetProperty("clientCapabilities").GetProperty("terminal").GetBoolean());
        Assert.Equal("jiangyu-studio", root.GetProperty("clientInfo").GetProperty("name").GetString());
    }

    [Fact]
    public void NewSessionRequest_IncludesMcpServers()
    {
        var request = new NewSessionRequest
        {
            Cwd = "/project",
            McpServers =
            [
                new McpServerConfig
                {
                    Name = "jiangyu",
                    Uri = "http://127.0.0.1:41697/mcp",
                },
            ],
        };

        var json = JsonSerializer.Serialize(request);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("/project", doc.RootElement.GetProperty("cwd").GetString());
        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.Equal(1, servers.GetArrayLength());
        Assert.Equal("jiangyu", servers[0].GetProperty("name").GetString());
    }

    [Fact]
    public void SessionUpdate_Serialises_AgentMessageChunk()
    {
        SessionUpdate update = new AgentMessageChunkUpdate
        {
            Content = new TextContentBlock { Text = "hello" },
        };

        var json = JsonSerializer.Serialize(update);
        Assert.Contains("\"sessionUpdate\":\"agent_message_chunk\"", json);
        Assert.Contains("\"text\":\"hello\"", json);
    }
}
