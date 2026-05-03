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
            "content": [{"type":"content","content":{"type":"text","text":"reading..."}}],
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
                {"content":"Step 1","status":"completed","priority":"high"},
                {"content":"Step 2","status":"in_progress","priority":"medium"}
            ]
        }
        """;
        var update = JsonSerializer.Deserialize<SessionUpdate>(json);

        var plan = Assert.IsType<PlanUpdate>(update);
        Assert.Equal(2, plan.Entries.Length);
        Assert.Equal("Step 1", plan.Entries[0].Content);
        Assert.Equal("high", plan.Entries[0].Priority);
    }

    [Fact]
    public void SessionUpdate_Deserialises_ToolCallUpdate()
    {
        var json = """
        {
            "sessionUpdate": "tool_call_update",
            "toolCallId": "tc-1",
            "content": [{"type":"diff","path":"/src/main.cs","newText":"new content","oldText":"old content"}],
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
        var json = """{"sessionUpdate":"config_option_update","configOptions":[{"type":"select","currentValue":"claude-4"}]}""";
        var update = JsonSerializer.Deserialize<SessionUpdate>(json);

        var cfg = Assert.IsType<ConfigOptionUpdate>(update);
        Assert.Single(cfg.ConfigOptions);
        Assert.Equal("select", cfg.ConfigOptions[0].GetProperty("type").GetString());
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
        var selected = new SelectedPermissionOutcome { OptionId = "allow_once" };
        var json = JsonSerializer.Serialize<PermissionOutcome>(selected);
        var rt = JsonSerializer.Deserialize<PermissionOutcome>(json);
        var result = Assert.IsType<SelectedPermissionOutcome>(rt);
        Assert.Equal("allow_once", result.OptionId);
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
                    Type = "http",
                    Url = "http://127.0.0.1:41697/mcp",
                    Headers = [],
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

    /// <summary>
    /// Regression: GitHub Copilot's ACP agent returns AuthMethod entries in
    /// the spec-correct {id, name, description?} shape. An earlier version
    /// of the schema required {type, url} instead, which threw at
    /// initialize and killed the connection. Lock the spec shape so a
    /// future refactor can't quietly reintroduce the wrong fields.
    /// </summary>
    [Fact]
    public void InitializeResponse_Deserialises_CopilotShapedAuthMethods()
    {
        var json = """
        {
            "protocolVersion": 1,
            "agentCapabilities": {
                "loadSession": false,
                "promptCapabilities": {}
            },
            "authMethods": [
                {
                    "id": "github-oauth",
                    "name": "GitHub OAuth",
                    "description": "Sign in with your GitHub account"
                },
                {
                    "id": "personal-access-token",
                    "name": "Personal Access Token"
                }
            ],
            "agentInfo": {
                "name": "github-copilot",
                "version": "1.0.39"
            }
        }
        """;

        var response = JsonSerializer.Deserialize<InitializeResponse>(json);

        Assert.NotNull(response);
        Assert.Equal(1, response.ProtocolVersion);
        Assert.NotNull(response.AuthMethods);
        Assert.Equal(2, response.AuthMethods.Length);

        var oauth = response.AuthMethods[0];
        Assert.Equal("github-oauth", oauth.Id);
        Assert.Equal("GitHub OAuth", oauth.Name);
        Assert.Equal("Sign in with your GitHub account", oauth.Description);

        var pat = response.AuthMethods[1];
        Assert.Equal("personal-access-token", pat.Id);
        Assert.Equal("Personal Access Token", pat.Name);
        Assert.Null(pat.Description);
    }

    [Fact]
    public void InitializeResponse_Deserialises_EmptyAuthMethods()
    {
        // Claude's ACP agent returns no auth methods. Make sure the empty
        // case still works after the schema change.
        var json = """
        {
            "protocolVersion": 1,
            "agentCapabilities": { "loadSession": false, "promptCapabilities": {} },
            "authMethods": []
        }
        """;

        var response = JsonSerializer.Deserialize<InitializeResponse>(json);
        Assert.NotNull(response);
        Assert.NotNull(response.AuthMethods);
        Assert.Empty(response.AuthMethods);
    }

    [Fact]
    public void AuthMethod_Rejects_MissingRequiredFields()
    {
        // The earlier failure mode: a payload with no `id` should be a hard
        // schema error, not silently ignored. Same for `name`.
        var noId = """{"name":"GitHub OAuth"}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AuthMethod>(noId));

        var noName = """{"id":"github-oauth"}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AuthMethod>(noName));
    }
}
