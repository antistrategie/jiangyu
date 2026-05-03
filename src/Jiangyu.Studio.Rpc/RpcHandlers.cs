using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Shared;

namespace Jiangyu.Studio.Rpc;

/// <summary>
/// Shared static handlers backing both Studio.Host (WebView RPC dispatcher) and
/// Jiangyu.Mcp (stdio MCP binary). Handlers here are pure
/// <c>(JsonElement?) -&gt; JsonElement</c> functions: no host window, no
/// streaming notifications, no UI. Anything window-bound (file dialogs,
/// pane management, streaming compile) lives in Studio.Host. Anything tagged
/// <see cref="McpToolAttribute"/> is exposed as an MCP tool by
/// <see cref="Mcp.McpServer"/>; untagged handlers here are the Host-only
/// half of an RPC whose body just doesn't depend on the window (e.g.
/// <c>readFile</c>, which the Host registers but ACP agents reach via
/// <c>fs/read_text_file</c> instead of MCP).
/// </summary>
public static partial class RpcHandlers
{
    internal static readonly JsonElement NullElement = JsonSerializer.SerializeToElement<object?>(null);

    /// <summary>
    /// Single-handler-at-a-time gate. The WebView dispatch is implicitly
    /// single-threaded by InfiniFrame, but MCP runs on whatever thread the
    /// MCP transport happens to use. Without serialisation, MCP and WebView
    /// would race on the shared statics (project root, asset/template
    /// indexes, agent manager). All handler invocations from either path
    /// take this lock.
    /// </summary>
    public static readonly Lock DispatchLock = new();

    [RpcType]
    internal sealed class FileEntry
    {
        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("path")]
        public required string Path { get; set; }

        [JsonPropertyName("isDirectory")]
        public required bool IsDirectory { get; set; }

        [JsonPropertyName("isIgnored")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsIgnored { get; set; }

        [JsonPropertyName("size")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long Size { get; set; }
    }
}
