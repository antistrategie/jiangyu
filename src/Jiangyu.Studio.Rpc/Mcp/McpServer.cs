using System.Reflection;
using System.Text.Json;
using Jiangyu.Shared;

namespace Jiangyu.Studio.Rpc.Mcp;

/// <summary>
/// Minimal MCP (Model Context Protocol) server. Reflects over
/// <see cref="RpcHandlers"/>, finds methods tagged with <see cref="McpToolAttribute"/>,
/// and exposes them as MCP tools. Transport-agnostic: callers feed it
/// JSON-RPC strings (one per request) via <see cref="HandleRequest"/> and
/// pump responses back out themselves. Used by Studio.Host (legacy
/// in-process route) and Jiangyu.Mcp (standalone stdio binary).
/// </summary>
public sealed class McpServer
{
    private readonly record struct ParamEntry(
        string Name,
        string Type,
        string Description,
        bool Required);

    private readonly record struct ToolEntry(
        string Name,
        string Description,
        MethodInfo Method,
        IReadOnlyList<ParamEntry> Params);

    private readonly Dictionary<string, ToolEntry> _tools = new(StringComparer.Ordinal);

    /// <summary>
    /// Discovers all static methods on <see cref="RpcHandlers"/> annotated
    /// with <see cref="McpToolAttribute"/> and registers them as MCP tools.
    /// </summary>
    public void DiscoverTools()
    {
        var methods = typeof(RpcHandlers).GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        foreach (var method in methods)
        {
            var attr = method.GetCustomAttribute<McpToolAttribute>();
            if (attr is null) continue;

            var paramAttrs = method.GetCustomAttributes<McpParamAttribute>()
                .Select(p => new ParamEntry(p.Name, p.Type, p.Description, p.Required))
                .ToList();

            _tools[attr.Name] = new ToolEntry(attr.Name, attr.Description, method, paramAttrs);
        }
    }

    /// <summary>
    /// Handles a single MCP JSON-RPC request and returns the JSON-RPC
    /// response body. Supports <c>initialize</c>, <c>tools/list</c>,
    /// and <c>tools/call</c>.
    /// </summary>
    public string HandleRequest(string body)
    {
        JsonElement request;
        try
        {
            using var doc = JsonDocument.Parse(body);
            request = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            return JsonRpcError(null, -32700, $"Parse error: {ex.Message}");
        }

        var id = request.TryGetProperty("id", out var idProp) ? idProp.Clone() : (JsonElement?)null;
        var method = request.TryGetProperty("method", out var methodProp) ? methodProp.GetString() : null;

        if (method is null)
            return JsonRpcError(id, -32600, "Missing 'method' field");

        return method switch
        {
            "initialize" => HandleInitialize(id, request),
            "notifications/initialized" => "", // client notification; no response
            "tools/list" => HandleToolsList(id),
            "tools/call" => HandleToolsCall(id, request),
            "resources/list" => HandleResourcesList(id),
            "resources/read" => HandleResourcesRead(id, request),
            "ping" => JsonRpcResult(id, JsonSerializer.SerializeToElement(new { })),
            _ => JsonRpcError(id, -32601, $"Method not found: {method}"),
        };
    }

    private static readonly string ServerVersion =
        typeof(McpServer).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

    /// <summary>
    /// Latest MCP protocol revision we know how to speak. The spec says the
    /// server should respond with the client's requested version when it
    /// can support it, otherwise fall back to a version it knows. The
    /// previous hardcoded "2025-11-25" wasn't a real MCP revision and
    /// likely caused Anthropic's claude-agent-sdk to silently reject our
    /// initialize response.
    /// </summary>
    private const string LatestSupportedMcpVersion = "2025-06-18";

    private static string HandleInitialize(JsonElement? id, JsonElement request)
    {
        // Echo the client's requested version when present; the spec
        // expects negotiation, and clients tend to drop connections that
        // get back a version they don't recognise.
        var requestedVersion = LatestSupportedMcpVersion;
        if (request.TryGetProperty("params", out var p) &&
            p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty("protocolVersion", out var v) &&
            v.ValueKind == JsonValueKind.String)
        {
            requestedVersion = v.GetString() ?? LatestSupportedMcpVersion;
        }

        var result = new
        {
            protocolVersion = requestedVersion,
            capabilities = new
            {
                tools = new { },
                resources = new { },
            },
            serverInfo = new
            {
                name = "jiangyu-studio",
                version = ServerVersion,
            },
        };
        return JsonRpcResult(id, JsonSerializer.SerializeToElement(result));
    }

    private string HandleToolsList(JsonElement? id)
    {
        var tools = new List<object>();
        foreach (var entry in _tools.Values)
        {
            tools.Add(new
            {
                name = entry.Name,
                description = entry.Description,
                inputSchema = BuildInputSchema(entry.Params),
            });
        }

        var result = new { tools };
        return JsonRpcResult(id, JsonSerializer.SerializeToElement(result));
    }

    private static object BuildInputSchema(IReadOnlyList<ParamEntry> parameters)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var p in parameters)
        {
            properties[p.Name] = new Dictionary<string, string>
            {
                ["type"] = p.Type,
                ["description"] = p.Description,
            };
            if (p.Required)
                required.Add(p.Name);
        }

        if (required.Count > 0)
        {
            return new
            {
                type = "object",
                properties,
                required,
            };
        }

        return new
        {
            type = "object",
            properties,
        };
    }

    private string HandleToolsCall(JsonElement? id, JsonElement request)
    {
        if (!request.TryGetProperty("params", out var callParams))
            return JsonRpcError(id, -32602, "Missing 'params'");

        var toolName = callParams.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        if (toolName is null)
            return JsonRpcError(id, -32602, "Missing tool 'name'");

        if (!_tools.TryGetValue(toolName, out var tool))
            return JsonRpcError(id, -32602, $"Unknown tool: {toolName}");

        var arguments = callParams.TryGetProperty("arguments", out var argsProp)
            ? argsProp
            : (JsonElement?)null;

        try
        {
            // Take the dispatch lock so MCP can't race with WebView-originated
            // RPCs that share the same static state (project root, indexes,
            // agent manager, etc.). The WebView path is already single-threaded
            // by InfiniFrame; this extends that ordering to MCP callers.
            JsonElement result;
            lock (RpcHandlers.DispatchLock)
                result = (JsonElement)tool.Method.Invoke(null, [arguments])!;

            return BuildSuccessResponse(id, result);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return ToolErrorResponse(id, toolName, ex.InnerException);
        }
        catch (Exception ex)
        {
            return ToolErrorResponse(id, toolName, ex);
        }
    }

    /// <summary>
    /// Tool-call exceptions are returned as MCP tool errors (so the agent can
    /// see them in-band) and also logged to the host's stderr so a developer
    /// can find the stack. Without the log, NREs and other host-side bugs
    /// silently become "tool error" text the LLM retries forever.
    /// </summary>
    private static string ToolErrorResponse(JsonElement? id, string toolName, Exception ex)
    {
        Console.Error.WriteLine($"[Mcp] tool '{toolName}' failed: {ex}");
        var content = new[]
        {
            new { type = "text", text = ex.Message },
        };
        var toolResult = new { content, isError = true };
        return JsonRpcResult(id, JsonSerializer.SerializeToElement(toolResult));
    }

    /// <summary>
    /// Builds the success-case tool response. Per the MCP spec, structured
    /// data goes in <c>structuredContent</c>; <c>content</c> still carries a
    /// human/agent-readable text rendering for clients that don't read
    /// structured output. Null / empty results return an empty content list
    /// rather than the literal string "{}", which conflates "no data" with
    /// "empty object".
    /// </summary>
    private static string BuildSuccessResponse(JsonElement? id, JsonElement result)
    {
        var isEmpty = result.ValueKind == JsonValueKind.Undefined ||
                      result.ValueKind == JsonValueKind.Null;

        if (isEmpty)
        {
            var emptyResult = new { content = Array.Empty<object>(), isError = false };
            return JsonRpcResult(id, JsonSerializer.SerializeToElement(emptyResult));
        }

        var content = new[]
        {
            new { type = "text", text = result.GetRawText() },
        };
        var withStructured = new
        {
            content,
            structuredContent = result,
            isError = false,
        };
        return JsonRpcResult(id, JsonSerializer.SerializeToElement(withStructured));
    }

    // Embedded docs live in this same assembly (Jiangyu.Studio.Rpc). Single
    // source of truth so both the WebView host and the standalone Mcp binary
    // serve the same markdown.
    private const string EmbeddedDocsPrefix = "Jiangyu.Studio.Rpc.docs.";
    private static readonly Assembly LibraryAssembly = typeof(McpServer).Assembly;

    /// <summary>
    /// Discovered at startup by scanning the assembly manifest for embedded doc
    /// resources. Each key is the suffix after the prefix (e.g.
    /// "reference.templates.md"), and the name is derived from the markdown
    /// front-matter title line or, failing that, from the filename.
    /// </summary>
    private static readonly List<(string Key, string Name)> EmbeddedDocs = DiscoverEmbeddedDocs();

    private static List<(string Key, string Name)> DiscoverEmbeddedDocs()
    {
        var result = new List<(string Key, string Name)>();
        foreach (var name in LibraryAssembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(EmbeddedDocsPrefix, StringComparison.Ordinal))
                continue;
            if (!name.EndsWith(".md", StringComparison.Ordinal))
                continue;

            var key = name[EmbeddedDocsPrefix.Length..];
            var friendly = DeriveFriendlyName(key);
            result.Add((key, friendly));
        }

        result.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
        return result;
    }

    private static string DeriveFriendlyName(string key)
    {
        // Try to extract the first markdown heading from the resource.
        var resourceName = EmbeddedDocsPrefix + key;
        using var stream = LibraryAssembly.GetManifestResourceStream(resourceName);
        if (stream is not null)
        {
            using var reader = new StreamReader(stream);
            for (var i = 0; i < 20; i++)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                if (line.StartsWith("# ", StringComparison.Ordinal))
                    return line[2..].Trim();
            }
        }

        // Fallback: turn "reference.replacements.audio.md" into "Reference Replacements Audio".
        var stem = key.EndsWith(".md", StringComparison.Ordinal) ? key[..^3] : key;
        return string.Join(' ', stem.Split('.').Select(
            s => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..].Replace('-', ' ') : s));
    }

    private static string? ReadEmbeddedDoc(string key)
    {
        var resourceName = EmbeddedDocsPrefix + key;
        using var stream = LibraryAssembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string HandleResourcesList(JsonElement? id)
    {
        var resources = new List<object>();

        // Embedded docs from site/
        foreach (var (key, name) in EmbeddedDocs)
        {
            resources.Add(new
            {
                uri = $"jiangyu://docs/{key}",
                name,
                description = $"Jiangyu documentation: {name}.",
                mimeType = "text/markdown",
            });
        }

        // Dynamic project context
        resources.Add(new
        {
            uri = "jiangyu://project-context",
            name = "Project Context",
            description = "Current project manifest, config status, and open project path.",
            mimeType = "application/json",
        });

        var result = new { resources };
        return JsonRpcResult(id, JsonSerializer.SerializeToElement(result));
    }

    private static string HandleResourcesRead(JsonElement? id, JsonElement request)
    {
        if (!request.TryGetProperty("params", out var readParams))
            return JsonRpcError(id, -32602, "Missing 'params'");

        var uri = readParams.TryGetProperty("uri", out var uriProp) ? uriProp.GetString() : null;
        if (uri is null)
            return JsonRpcError(id, -32602, "Missing 'uri'");

        if (uri == "jiangyu://project-context")
            return ReadProjectContextResource(id);

        if (uri.StartsWith("jiangyu://docs/", StringComparison.Ordinal))
        {
            var key = uri["jiangyu://docs/".Length..];
            var text = ReadEmbeddedDoc(key);
            if (text is not null)
            {
                var contents = new[]
                {
                    new { uri, mimeType = "text/markdown", text },
                };
                return JsonRpcResult(id, JsonSerializer.SerializeToElement(new { contents }));
            }
        }

        return JsonRpcError(id, -32602, $"Unknown resource: {uri}");
    }

    private static string ReadProjectContextResource(JsonElement? id)
    {
        var context = new Dictionary<string, object?>();

        var projectRoot = RpcContext.ProjectRoot;
        context["projectRoot"] = projectRoot is not null
            ? RpcHelpers.NormaliseSeparators(projectRoot)
            : null;

        if (projectRoot is not null)
        {
            var manifestPath = Path.Combine(projectRoot, "jiangyu.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    context["manifest"] = JsonSerializer.Deserialize<JsonElement>(
                        File.ReadAllText(manifestPath));
                }
                catch
                {
                    context["manifest"] = null;
                }
            }
        }

        var contents = new[]
        {
            new
            {
                uri = "jiangyu://project-context",
                mimeType = "application/json",
                text = JsonSerializer.Serialize(context),
            },
        };

        return JsonRpcResult(id, JsonSerializer.SerializeToElement(new { contents }));
    }

    // --- JSON-RPC helpers ---

    private static string JsonRpcResult(JsonElement? id, JsonElement result)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        if (id.HasValue)
        {
            writer.WritePropertyName("id");
            id.Value.WriteTo(writer);
        }
        else
        {
            writer.WriteNull("id");
        }
        writer.WritePropertyName("result");
        result.WriteTo(writer);
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string JsonRpcError(JsonElement? id, int code, string message)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        if (id.HasValue)
        {
            writer.WritePropertyName("id");
            id.Value.WriteTo(writer);
        }
        else
        {
            writer.WriteNull("id");
        }
        writer.WriteStartObject("error");
        writer.WriteNumber("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
