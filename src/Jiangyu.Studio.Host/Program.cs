using System.Drawing;
using InfiniFrame;
using InfiniFrame.WebServer;

namespace Jiangyu.Studio.Host;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsLinux()) LinuxDesktopEntry.Ensure();

        var builder = InfiniFrameWebApplication.CreateBuilder(args);

        builder.Window
            .SetTitle("Jiangyu Studio")
            .SetSize(new Size(1680, 1050))
            .SetIconFile(Path.Combine(AppContext.BaseDirectory, "icon.png"))
            .Center()
            .RegisterWebMessageReceivedHandler(
                (window, message) =>
                    RpcDispatcher.HandleMessage(window, message, window.SendWebMessage));

        // In dev mode, point the webview at the Vite dev server for HMR.
        var devUrl = Environment.GetEnvironmentVariable("JIANGYU_DEV_URL");
        if (!string.IsNullOrEmpty(devUrl))
        {
            builder.Window.SetStartUrl(devUrl);
        }

        var app = builder.Build();

        // Preload template caches on a background thread so the template browser
        // doesn't freeze the app when it first mounts.
        _ = Task.Run(() => RpcDispatcher.PreloadTemplateCaches());

        app.UseAutoServerClose();
        app.WebApp.UseStaticFiles();

        app.Run();
    }
}
