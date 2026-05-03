namespace Jiangyu.Acp.JsonRpc;

/// <summary>
/// Wire framing for JSON-RPC messages over stdio.
/// </summary>
public enum FramingMode
{
    /// <summary>
    /// LSP-style Content-Length headers. Used by MCP and some ACP agents.
    /// </summary>
    ContentLength,

    /// <summary>
    /// Newline-delimited JSON (one JSON object per line, no headers).
    /// Used by the ACP JS SDK and most ACP agents in practice.
    /// </summary>
    NdJson,
}
