using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Jiangyu.Studio.Rpc.Mcp;

namespace Jiangyu.Studio.Host.Mcp;

/// <summary>
/// Mounts the HTTP MCP transport at <c>POST /mcp</c>. Used by ACP agents
/// (notably GitHub Copilot) that silently reject stdio MCP entries — they
/// only honour http/sse. Same <see cref="McpServer"/> backs both this and
/// the standalone <c>jiangyu-mcp</c> stdio binary; same handlers, same
/// dispatch lock, same observable behaviour.
/// </summary>
public static class McpEndpoint
{
    /// <summary>
    /// Body cap. The largest tool inputs we ship (KDL parse / serialise) are
    /// individual files; nothing legitimate goes past a few hundred KiB. 8
    /// MiB is generous and small enough that an accidental loop can't
    /// exhaust the host's heap.
    /// </summary>
    public const int MaxBodyBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Maps <c>POST /mcp</c> on the given route builder. Authentication is a
    /// per-launch random bearer token; agents receive it in the
    /// <c>Authorization</c> header field of the <see cref="Jiangyu.Acp.Schema.McpServerConfig"/>
    /// we hand them via <c>session/new</c>. The token never round-trips
    /// through the WebView origin or localStorage, so a malicious page
    /// loaded in the WebView (or a stray cross-origin POST from any browser
    /// on the same host) can't reach the endpoint.
    /// </summary>
    public static void Map(IEndpointRouteBuilder routes, McpServer server, string bearerToken)
    {
        routes.MapPost("/mcp", async context =>
        {
            var auth = context.Request.Headers.Authorization.ToString();
            if (auth != $"Bearer {bearerToken}")
            {
                context.Response.StatusCode = 401;
                return;
            }

            if (context.Request.ContentLength is long len && len > MaxBodyBytes)
            {
                context.Response.StatusCode = 413;
                return;
            }

            // Cap the read even when Content-Length is absent or wrong.
            // EnableBuffering tees the request body to memory or a temp
            // file once the threshold is exceeded; bufferLimit is the
            // hard cap that throws when reached.
            context.Request.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: MaxBodyBytes);

            string body;
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                body = await reader.ReadToEndAsync();
            }
            catch (IOException)
            {
                // EnableBuffering throws when the stream exceeds bufferLimit.
                context.Response.StatusCode = 413;
                return;
            }

            var response = server.HandleRequest(body);
            if (string.IsNullOrEmpty(response))
            {
                context.Response.StatusCode = 204;
                return;
            }
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(response);
        });
    }
}
