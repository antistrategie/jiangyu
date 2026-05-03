using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using InfiniFrame;
using InfiniFrame.WebServer;
using Jiangyu.Studio.Host.Acp;
using Jiangyu.Studio.Host.Infrastructure;
using Jiangyu.Studio.Host.Mcp;
using Jiangyu.Studio.Host.Rpc;
using Jiangyu.Studio.Rpc.Mcp;

namespace Jiangyu.Studio.Host;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsLinux()) LinuxDesktopEntry.Ensure();

        // Always allocate the ASP.NET host's port up front, even in dev. The
        // WebView origin can be the Vite dev server (JIANGYU_DEV_URL) for HMR,
        // but everything we mount on the .NET host (the /mcp endpoint we hand
        // to ACP agents) lives on hostUrl regardless. Conflating the two
        // routed Copilot's HTTP MCP requests at Vite (5173), which doesn't
        // proxy /mcp, and the connection silently failed.
        const int preferredPort = 41697;
        var hostPort = IsPortAvailable(preferredPort) ? preferredPort : FindFreePort();
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", $"http://127.0.0.1:{hostPort}");
        var hostUrl = $"http://127.0.0.1:{hostPort}";

        // The WebView loads from Vite during dev (HMR + faster rebuilds);
        // otherwise it loads from the .NET host's static files. Either way
        // a stable origin keeps localStorage intact across launches.
        var devUrl = Environment.GetEnvironmentVariable("JIANGYU_DEV_URL");
        var startUrl = string.IsNullOrEmpty(devUrl) ? hostUrl : devUrl;

        var builder = InfiniFrameWebApplication.CreateBuilder(args);

        // Pin working directory to the app directory so standalone runs
        // (e.g. from ~/Downloads) resolve wwwroot/ relative to the binary.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        builder.WindowBuilder
            .SetTitle("Jiangyu Studio")
            .SetSize(new Size(1680, 1050))
            .SetIconFile(Path.Combine(AppContext.BaseDirectory, "icon.png"))
            .Center()
            .SetStartUrl(startUrl)
            .RegisterWebMessageReceivedHandler(
                (window, message) =>
                {
                    // InfiniFrame 0.11.0 wraps messages in an envelope:
                    // {"id":"…","command":"Post","data":"<payload>","version":2}
                    // Unwrap the data field to get the raw RPC JSON.
                    var payload = UnwrapEnvelope(message);
                    if (payload is not null)
                        RpcDispatcher.HandleMessage(window, payload, window.SendWebMessage);
                });

        var app = builder.Build();

        // Preload template caches on a background thread so the template browser
        // doesn't freeze the app when it first mounts.
        _ = Task.Run(RpcDispatcher.PreloadTemplateCaches);

        app.UseAutoServerClose();

        // MCP runs over both transports because no agent supports both:
        // - Anthropic's claude-agent-sdk silently DROPS HTTP MCP configs
        //   (despite advertising mcpCapabilities.http=true) and only honours
        //   stdio. The sibling jiangyu-mcp exe handles that path.
        // - GitHub Copilot's ACP integration silently REJECTS stdio MCP configs
        //   ("Rejecting non-http/sse MCP server" in its logs) and only honours
        //   http/sse. The /mcp endpoint below handles that path.
        // We send both entries in session/new mcpServers; each agent picks the
        // one it accepts and drops the other.
        var mcpToken = GenerateMcpToken();
        AgentProcessManager.HttpMcpUrl = $"{hostUrl}/mcp";
        AgentProcessManager.HttpMcpToken = mcpToken;
        var mcpServer = new McpServer();
        mcpServer.DiscoverTools();
        McpEndpoint.Map(app.WebApp, mcpServer, mcpToken);

        // The host pins itself to a stable loopback port (above) so the WebView
        // origin survives across launches and localStorage stays intact. The
        // tradeoff is that the WebView's HTTP cache also survives, so without
        // explicit cache headers Chromium falls back to heuristic freshness on
        // index.html and serves the previous version's bundle until the user
        // hard-refreshes. Vite hashes everything under /assets, so those are
        // safe to cache forever; index.html and other unhashed files must
        // always revalidate so upgrades take effect on the next launch.
        app.WebApp.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                var path = ctx.Context.Request.Path.Value;
                var headers = ctx.Context.Response.Headers;
                headers.CacheControl = path != null && path.StartsWith("/assets/", StringComparison.Ordinal)
                    ? "public, max-age=31536000, immutable"
                    : "no-cache, must-revalidate";
            },
        });

        app.Run();
    }

    /// <summary>
    /// 256-bit random bearer token, base64url-encoded, regenerated each launch.
    /// Used to gate the HTTP MCP endpoint we hand to ACP agents in
    /// <c>session/new</c>. Token sees stdio between the host and the agent
    /// only; nothing serialises it to disk.
    /// </summary>
    private static string GenerateMcpToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// InfiniFrame 0.11.0 wraps web messages in an envelope:
    /// <c>{"id":"…","command":"Post","data":"&lt;payload&gt;","version":2}</c>.
    /// Extract the <c>data</c> field so the RPC dispatcher sees raw JSON.
    /// </summary>
    internal static string? UnwrapEnvelope(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.String)
            {
                return data.GetString();
            }
        }
        catch
        {
            // Not valid JSON; pass through as-is.
        }

        return message;
    }
}
