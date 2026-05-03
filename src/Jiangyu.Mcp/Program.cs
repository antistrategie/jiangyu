using System.Text.Json;
using Jiangyu.Studio.Rpc;
using Jiangyu.Studio.Rpc.Mcp;

namespace Jiangyu.Mcp;

/// <summary>
/// Standalone stdio MCP server. Spawned by ACP agents as their MCP
/// transport: agent inherits its own working directory (= project root),
/// so we seed <see cref="RpcContext.ProjectRoot"/> from
/// <see cref="Environment.CurrentDirectory"/> at startup. Reads
/// newline-framed JSON-RPC from stdin, dispatches each request through
/// <see cref="McpServer"/>, writes the response to stdout. Stderr is
/// reserved for logging per the MCP spec; nothing the agent sees over
/// stdio.
///
/// Single-file published as a sibling executable to <c>jiangyu-studio</c>;
/// AgentProcessManager resolves it via <c>AppContext.BaseDirectory</c>.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        _ = args;

        // The agent's session/new sets the agent process cwd to the project
        // root and the agent inherits that into us. RpcContext.ProjectRoot
        // is what every handler reads to gate filesystem ops, so seed it
        // before we start dispatching.
        RpcContext.ProjectRoot = Path.GetFullPath(Environment.CurrentDirectory);
        Console.Error.WriteLine($"[jiangyu-mcp] starting; cwd = {RpcContext.ProjectRoot}");

        var server = new McpServer();
        server.DiscoverTools();

        var stdin = Console.In;
        var stdout = Console.Out;
        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            string response;
            try
            {
                response = server.HandleRequest(line);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[jiangyu-mcp] dispatch error: {ex}");
                // Best-effort error response; we may not have the request id.
                response = """{"jsonrpc":"2.0","id":null,"error":{"code":-32603,"message":""" +
                    JsonSerializer.Serialize(ex.Message) + "}}";
            }

            // Notifications produce no response body; just consume them silently.
            if (string.IsNullOrEmpty(response)) continue;

            stdout.WriteLine(response);
            stdout.Flush();
        }

        Console.Error.WriteLine("[jiangyu-mcp] stdin closed; exiting");
        return 0;
    }
}
