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

        // TODO: remove when InfiniFrame drops the Blazor IJSRuntime dependency
        // for non-Blazor hosts. InfiniFrame 0.11.0's AddInfiniFrameJs registers
        // InfiniFrameJs which depends on IJSRuntime; we don't use Blazor, so
        // register a no-op implementation to satisfy the DI container.
        builder.Services.AddScoped<Microsoft.JSInterop.IJSRuntime, NullJSRuntime>();

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

        // Preload template caches on a background thread so the template browser
        // doesn't freeze the app when it first mounts.
        _ = Task.Run(RpcDispatcher.PreloadTemplateCaches);

        app.UseAutoServerClose();

        app.WebApp.MapPost("/mcp", async context =>
        {
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
