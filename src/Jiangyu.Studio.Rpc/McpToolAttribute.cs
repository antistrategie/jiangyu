namespace Jiangyu.Studio.Rpc;

/// <summary>
/// Marks an RPC handler method for exposure as an MCP tool. The in-process
/// MCP server discovers these at startup via reflection and wraps each
/// handler so any ACP agent can call it as a standard MCP tool.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class McpToolAttribute(string name, string description) : Attribute
{
    /// <summary>MCP tool name (e.g. "jiangyu_templates_search").</summary>
    public string Name { get; } = name;

    /// <summary>
    /// Human-readable description shown to the LLM. Should be detailed enough
    /// that a model can decide when and how to call the tool.
    /// </summary>
    public string Description { get; } = description;

    /// <summary>
    /// True for tools that may run for many seconds (e.g. compile). The MCP
    /// dispatch skips its global lock for these so an in-flight long-running
    /// tool doesn't block every concurrent WebView RPC. The tool is then
    /// responsible for its own concurrency (typically a per-tool gate).
    /// Off by default.
    /// </summary>
    public bool LongRunning { get; set; }

    /// <summary>
    /// True for tools that drive the running game over the live Studio bridge
    /// (the inspect/gate/verbs/ui-capture family). The MCP server appends a
    /// standard "requires the live bridge" note to the tool's description so the
    /// model knows the precondition, and the tool itself fails with a clear error
    /// when MENACE isn't running with the <c>bridge</c> dev flag set. Off by default.
    /// </summary>
    public bool RequiresBridge { get; set; }
}

/// <summary>
/// Declares a parameter for an MCP tool. Applied alongside <see cref="McpToolAttribute"/>
/// on the same handler method. The MCP server reads these at discovery time
/// and builds a JSON Schema <c>inputSchema</c> for each tool.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class McpParamAttribute(string name, string type, string description) : Attribute
{
    /// <summary>Parameter name (matches the JSON property the handler reads).</summary>
    public string Name { get; } = name;

    /// <summary>JSON Schema type: "string", "integer", "number", "boolean", "object".</summary>
    public string Type { get; } = type;

    /// <summary>Human-readable description of the parameter.</summary>
    public string Description { get; } = description;

    /// <summary>Whether this parameter is required. Defaults to false.</summary>
    public bool Required { get; set; }
}
