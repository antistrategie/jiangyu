using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using InfiniFrame;
using InfiniFrame.WebServer;
using Jiangyu.Studio.Host.Infrastructure;
using Jiangyu.Studio.Host.Mcp;
using Jiangyu.Studio.Host.Rpc;

namespace Jiangyu.Studio.Host;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsLinux()) LinuxDesktopEntry.Ensure();

        // In dev mode, point the webview at the Vite dev server for HMR.
        var devUrl = Environment.GetEnvironmentVariable("JIANGYU_DEV_URL");

        // In production, prefer a stable loopback port so the WebView's origin
        // (scheme + host + port) is the same across launches. localStorage is
        // origin-scoped, so a random port would wipe recent projects, sidebar
        // width, per-project layouts and pane windows on every relaunch. Falls
        // back to a random port if the preferred one is taken (e.g. a second
        // instance is already running).
        if (string.IsNullOrEmpty(devUrl))
        {
            const int preferredPort = 41697;
            var port = IsPortAvailable(preferredPort) ? preferredPort : FindFreePort();
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", $"http://127.0.0.1:{port}");
            devUrl = $"http://127.0.0.1:{port}";
        }

        var builder = InfiniFrameWebApplication.CreateBuilder(args);

        // Pin working directory to the app directory so standalone runs
        // (e.g. from ~/Downloads) resolve wwwroot/ relative to the binary.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        builder.WindowBuilder
            .SetTitle("Jiangyu Studio")
            .SetSize(new Size(1680, 1050))
            .SetIconFile(Path.Combine(AppContext.BaseDirectory, "icon.png"))
            .Center()
            .SetStartUrl(devUrl)
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

        // MCP tool server: expose Jiangyu domain tools to ACP agents via HTTP.
        // Same process, same handlers; the agent connects to this endpoint.
        var mcpServer = new McpServer();
        mcpServer.DiscoverTools();

        // Tell the agent dispatcher which port the MCP endpoint is on,
        // so it can pass the URL when creating ACP sessions.
        if (Uri.TryCreate(devUrl, UriKind.Absolute, out var devUri))
            RpcDispatcher.McpPort = devUri.Port;

        // Preload template caches on a background thread so the template browser
        // doesn't freeze the app when it first mounts.
        _ = Task.Run(RpcDispatcher.PreloadTemplateCaches);

        app.UseAutoServerClose();

        // Same-origin gate. Browser tabs can issue cross-origin POSTs without
        // a preflight (Content-Type: text/plain qualifies as "simple" CORS),
        // so they could trigger arbitrary tool calls if /mcp were unguarded.
        // Reject any cross-origin request; agents launched as stdio
        // subprocesses fetch via this URL and don't send Origin/Referer at
        // all, so a missing header is allowed. Browser-issued same-origin
        // requests from the WebView itself carry our own origin.
        app.WebApp.MapPost("/mcp", async context =>
        {
            var origin = context.Request.Headers.Origin.ToString();
            var referer = context.Request.Headers.Referer.ToString();
            if (!IsAcceptableOrigin(origin, devUrl) || !IsAcceptableReferer(referer, devUrl))
            {
                context.Response.StatusCode = 403;
                return;
            }

            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            var response = mcpServer.HandleRequest(body);
            if (string.IsNullOrEmpty(response))
            {
                context.Response.StatusCode = 204;
                return;
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(response);
        });

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
    /// True when the request's Origin header is absent (typical of stdio
    /// subprocess agents using HttpClient) or matches the host's own origin.
    /// </summary>
    private static bool IsAcceptableOrigin(string origin, string ourOrigin)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        return string.Equals(origin, ourOrigin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the request's Referer is absent or shares scheme+host+port
    /// with our origin. We compare authorities rather than literal strings so
    /// a Referer of "<c>http://127.0.0.1:PORT/some/path</c>" still passes.
    /// </summary>
    private static bool IsAcceptableReferer(string referer, string ourOrigin)
    {
        if (string.IsNullOrEmpty(referer)) return true;
        if (!Uri.TryCreate(referer, UriKind.Absolute, out var refererUri)) return false;
        if (!Uri.TryCreate(ourOrigin, UriKind.Absolute, out var ourUri)) return false;
        return refererUri.Scheme == ourUri.Scheme &&
               refererUri.Host == ourUri.Host &&
               refererUri.Port == ourUri.Port;
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
