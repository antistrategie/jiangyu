using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Shared;
using static Jiangyu.Studio.Rpc.RpcHelpers;

namespace Jiangyu.Studio.Rpc;

public static partial class RpcHandlers
{
    [McpTool("jiangyu_docs_list",
        "List Jiangyu reference docs (manifest schema, KDL template operations, replacement formats, troubleshooting). Returns {\"docs\": [{\"key\": \"reference.templates\", \"title\": \"...\"}, ...]}. Tool form of the MCP `resources/list` surface — useful for clients that don't expose resources to the agent (e.g. Copilot).")]
    internal static JsonElement DocsList(JsonElement? __)
    {
        var docs = EmbeddedDocs.All
            .Select(d => new DocListEntry { Key = d.Key, Title = d.Name })
            .ToList();
        return JsonSerializer.SerializeToElement(new DocsListResult { Docs = docs });
    }

    [McpTool("jiangyu_docs_read",
        "Fetch the full markdown of one Jiangyu reference doc by its key (the value from jiangyu_docs_list's `key` field, e.g. \"reference.templates.md\"). Returns {\"text\": \"...\"} or an error if the key is unknown. Tool form of the MCP `resources/read` surface.")]
    [McpParam("key", "string", "Doc key from jiangyu_docs_list (e.g. \"reference.templates.md\").", Required = true)]
    internal static JsonElement DocsRead(JsonElement? parameters)
    {
        var key = RequireString(parameters, "key");
        var text = EmbeddedDocs.Read(key)
            ?? throw new InvalidOperationException($"Unknown docs key: {key}. Call jiangyu_docs_list to see available keys.");
        return JsonSerializer.SerializeToElement(new DocsReadResult { Text = text });
    }

    [RpcType]
    internal sealed class DocsListResult
    {
        [JsonPropertyName("docs")]
        public required List<DocListEntry> Docs { get; set; }
    }

    [RpcType]
    internal sealed class DocListEntry
    {
        [JsonPropertyName("key")]
        public required string Key { get; set; }

        [JsonPropertyName("title")]
        public required string Title { get; set; }
    }

    [RpcType]
    internal sealed class DocsReadResult
    {
        [JsonPropertyName("text")]
        public required string Text { get; set; }
    }
}
