using System.Drawing;
using InfiniFrame;
using InfiniFrame.WebServer;

namespace Jiangyu.Studio.Host;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var builder = InfiniFrameWebApplication.CreateBuilder(args);

        builder.Window
            .SetTitle("Jiangyu Studio")
            .SetSize(new Size(1680, 1050))
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

        app.UseAutoServerClose();
        app.WebApp.UseStaticFiles();

        app.Run();
    }
}
