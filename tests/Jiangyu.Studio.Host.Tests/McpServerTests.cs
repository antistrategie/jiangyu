using System.Text.Json;

namespace Jiangyu.Studio.Host.Tests;

public class McpServerTests
{
    private static McpServer CreateServer()
    {
        var server = new McpServer();
        server.DiscoverTools();
        return server;
    }

    private static string MakeRequest(string method, int id = 1, string? paramsJson = null)
    {
        var paramsPart = paramsJson is not null ? $""","params":{paramsJson}""" : "";
        return $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"{{paramsPart}}}""";
    }

    [Fact]
    public void Initialize_ReturnsProtocolVersion()
    {
        var server = CreateServer();
        var response = server.HandleRequest(MakeRequest("initialize",
            paramsJson: """{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}"""));

        var doc = JsonDocument.Parse(response);
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("2025-11-25", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("jiangyu-studio", result.GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public void Initialize_ReturnsToolsAndResourcesCapabilities()
    {
        var server = CreateServer();
        var response = server.HandleRequest(MakeRequest("initialize",
            paramsJson: """{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}"""));

        var doc = JsonDocument.Parse(response);
        var caps = doc.RootElement.GetProperty("result").GetProperty("capabilities");
        Assert.True(caps.TryGetProperty("tools", out _));
        Assert.True(caps.TryGetProperty("resources", out _));
    }

    [Fact]
    public void ToolsList_ReturnsAnnotatedHandlers()
    {
        var server = CreateServer();
        var response = server.HandleRequest(MakeRequest("tools/list"));

        var doc = JsonDocument.Parse(response);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");
        Assert.True(tools.GetArrayLength() > 0, "Expected at least one tool");

        // Every tool must have name, description, inputSchema
        foreach (var tool in tools.EnumerateArray())
        {
            Assert.True(tool.TryGetProperty("name", out var name), "Tool missing 'name'");
            Assert.False(string.IsNullOrEmpty(name.GetString()));
            Assert.True(tool.TryGetProperty("description", out _), $"Tool '{name}' missing 'description'");
            Assert.True(tool.TryGetProperty("inputSchema", out var schema), $"Tool '{name}' missing 'inputSchema'");
            Assert.Equal("object", schema.GetProperty("type").GetString());
        }
    }

    [Fact]
    public void ToolsList_IncludesInputSchemaWithProperties()
    {
        var server = CreateServer();
        var response = server.HandleRequest(MakeRequest("tools/list"));
        var doc = JsonDocument.Parse(response);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");

        // Find jiangyu_read_file and verify its schema has path (required),
        // startLine and endLine (optional).
        JsonElement? readFileTool = null;
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.GetProperty("name").GetString() == "jiangyu_read_file")
            {
                readFileTool = tool;
                break;
            }
        }

        Assert.True(readFileTool.HasValue, "jiangyu_read_file tool not found");

        var schema = readFileTool.Value.GetProperty("inputSchema");
        var props = schema.GetProperty("properties");
        Assert.True(props.TryGetProperty("path", out var pathProp));
        Assert.Equal("string", pathProp.GetProperty("type").GetString());
        Assert.True(props.TryGetProperty("startLine", out var startProp));
        Assert.Equal("integer", startProp.GetProperty("type").GetString());
        Assert.True(props.TryGetProperty("endLine", out _));

        var required = schema.GetProperty("required");
        var requiredNames = new List<string>();
        foreach (var r in required.EnumerateArray())
            requiredNames.Add(r.GetString()!);
        Assert.Contains("path", requiredNames);
        Assert.DoesNotContain("startLine", requiredNames);
        Assert.DoesNotContain("endLine", requiredNames);
    }

    [Fact]
    public void ToolsList_NoParamsToolHasEmptyProperties()
    {
        var server = CreateServer();
        var response = server.HandleRequest(MakeRequest("tools/list"));
        var doc = JsonDocument.Parse(response);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");

        // jiangyu_compile_summary has no parameters.
        JsonElement? compileTool = null;
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.GetProperty("name").GetString() == "jiangyu_compile_summary")
            {
                compileTool = tool;
                break;
            }
        }

        Assert.True(compileTool.HasValue, "jiangyu_compile_summary tool not found");
        var schema = compileTool.Value.GetProperty("inputSchema");
        var props = schema.GetProperty("properties");
        Assert.Equal(0, props.EnumerateObject().Count());
        Assert.False(schema.TryGetProperty("required", out _));
    }

    [Fact]
    public void ToolsCall_UnknownTool_ReturnsError()
    {
        var server = CreateServer();
        var response = server.HandleRequest(MakeRequest("tools/call",
            paramsJson: """{"name":"nonexistent_tool"}"""));

        var doc = JsonDocument.Parse(response);
        var error = doc.RootElement.GetProperty("error");
        Assert.Contains("Unknown tool", error.GetProperty("message").GetString());
    }

    [Fact]
    public void ToolsCall_MissingName_ReturnsError()
    {
        var server = CreateServer();
        var response = server.HandleRequest(MakeRequest("tools/call",
            paramsJson: """{}"""));

        var doc = JsonDocument.Parse(response);
        var error = doc.RootElement.GetProperty("error");
        Assert.Contains("Missing tool 'name'", error.GetProperty("message").GetString());
    }

    [Fact]
    public void ToolsCall_MissingParams_ReturnsError()
    {
        var server = CreateServer();
        var response = server.HandleRequest(MakeRequest("tools/call"));

        var doc = JsonDocument.Parse(response);
        var error = doc.RootElement.GetProperty("error");
        Assert.Contains("Missing 'params'", error.GetProperty("message").GetString());
    }

    [Fact]
    public void Ping_ReturnsEmptyObject()
    {
        var server = CreateServer();
        var response = server.HandleRequest(MakeRequest("ping"));

        var doc = JsonDocument.Parse(response);
        Assert.True(doc.RootElement.TryGetProperty("result", out var result));
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
    }

    [Fact]
    public void UnknownMethod_ReturnsMethodNotFound()
    {
        var server = CreateServer();
        var response = server.HandleRequest(MakeRequest("bogus/method"));

        var doc = JsonDocument.Parse(response);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal(-32601, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public void MissingMethodField_ReturnsInvalidRequest()
    {
        var server = CreateServer();
        var response = server.HandleRequest("""{"jsonrpc":"2.0","id":1}""");

        var doc = JsonDocument.Parse(response);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal(-32600, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public void MalformedJson_ReturnsParseError()
    {
        var server = CreateServer();
        var response = server.HandleRequest("not json");

        var doc = JsonDocument.Parse(response);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal(-32700, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public void NotificationsInitialized_ReturnsEmpty()
    {
        var server = CreateServer();
        var response = server.HandleRequest(MakeRequest("notifications/initialized"));

        Assert.Equal("", response);
    }

    [Fact]
    public void ResourcesList_ContainsProjectContext()
    {
        var server = CreateServer();
        var response = server.HandleRequest(MakeRequest("resources/list"));

        var doc = JsonDocument.Parse(response);
        var resources = doc.RootElement.GetProperty("result").GetProperty("resources");

        var hasProjectContext = false;
        foreach (var resource in resources.EnumerateArray())
        {
            if (resource.GetProperty("uri").GetString() == "jiangyu://project-context")
            {
                hasProjectContext = true;
                break;
            }
        }
        Assert.True(hasProjectContext, "Expected jiangyu://project-context resource");
    }

    [Fact]
    public void ResourcesList_ContainsEmbeddedDocs()
    {
        var server = CreateServer();
        var response = server.HandleRequest(MakeRequest("resources/list"));

        var doc = JsonDocument.Parse(response);
        var resources = doc.RootElement.GetProperty("result").GetProperty("resources");

        var docCount = 0;
        foreach (var resource in resources.EnumerateArray())
        {
            var uri = resource.GetProperty("uri").GetString();
            if (uri?.StartsWith("jiangyu://docs/") == true)
                docCount++;
        }
        Assert.True(docCount > 0, "Expected at least one embedded doc resource");
    }

    [Fact]
    public void ResourcesRead_UnknownUri_ReturnsError()
    {
        var server = CreateServer();
        var response = server.HandleRequest(MakeRequest("resources/read",
            paramsJson: """{"uri":"jiangyu://docs/nonexistent.md"}"""));

        var doc = JsonDocument.Parse(response);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void ResourcesRead_MissingUri_ReturnsError()
    {
        var server = CreateServer();
        var response = server.HandleRequest(MakeRequest("resources/read",
            paramsJson: """{}"""));

        var doc = JsonDocument.Parse(response);
        var error = doc.RootElement.GetProperty("error");
        Assert.Contains("Missing 'uri'", error.GetProperty("message").GetString());
    }

    [Fact]
    public void ResponsesIncludeRequestId()
    {
        var server = CreateServer();
        var response = server.HandleRequest(MakeRequest("ping", id: 42));

        var doc = JsonDocument.Parse(response);
        Assert.Equal(42, doc.RootElement.GetProperty("id").GetInt32());
    }
}
