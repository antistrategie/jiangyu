using System.Drawing;
using System.Net;
using System.Net.Sockets;
using InfiniFrame;
using InfiniFrame.WebServer;

namespace Jiangyu.Studio.Host;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsLinux()) LinuxDesktopEntry.Ensure();

        // In dev mode, point the webview at the Vite dev server for HMR.
        var devUrl = Environment.GetEnvironmentVariable("JIANGYU_DEV_URL");

        // In production, bind to a random free loopback port so the app
        // window is the only practical consumer.
        if (string.IsNullOrEmpty(devUrl))
        {
            var port = FindFreePort();
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", $"http://127.0.0.1:{port}");
            devUrl = $"http://127.0.0.1:{port}";
        }

        var builder = InfiniFrameWebApplication.CreateBuilder(args);

        // Pin working directory to the app directory so standalone runs
        // (e.g. from ~/Downloads) resolve wwwroot/ relative to the binary.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        builder.Window
            .SetTitle("Jiangyu Studio")
            .SetSize(new Size(1680, 1050))
            .SetIconFile(Path.Combine(AppContext.BaseDirectory, "icon.png"))
            .Center()
            .SetStartUrl(devUrl)
            .RegisterWebMessageReceivedHandler(
                (window, message) =>
                    RpcDispatcher.HandleMessage(window, message, window.SendWebMessage));

        var app = builder.Build();

        // Preload template caches on a background thread so the template browser
        // doesn't freeze the app when it first mounts.
        _ = Task.Run(() => RpcDispatcher.PreloadTemplateCaches());

        app.UseAutoServerClose();
        app.WebApp.UseStaticFiles();

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
}
