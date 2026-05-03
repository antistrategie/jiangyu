using System.Text.Json;
using InfiniFrame;
using static Jiangyu.Studio.Rpc.RpcHelpers;

namespace Jiangyu.Studio.Host.Rpc;

public static partial class RpcDispatcher
{
    /// <summary>
    /// Pick-a-directory dialog. Window-bound (native chrome) so it stays in
    /// Host instead of moving to Studio.Rpc with the asset MCP tools.
    /// </summary>
    private static JsonElement HandlePickDirectory(IInfiniFrameWindow window, JsonElement? parameters)
    {
        var title = TryGetString(parameters, "title") ?? "Select directory";
        var initial = TryGetString(parameters, "initial");
        var results = window.ShowOpenFolder(title, defaultPath: initial);
        var path = results.FirstOrDefault(p => p is not null);
        return JsonSerializer.SerializeToElement(path);
    }
}
