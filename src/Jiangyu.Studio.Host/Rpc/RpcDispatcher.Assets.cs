using System.Text.Json;
using InfiniFrame;

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
        var path = PickDirectory(window.Features.FilePickerDialogs, title, TryGetString(parameters, "initial"));
        return JsonSerializer.SerializeToElement(path);
    }

    internal static string? PickDirectory(IFilePickerDialogsInfiniFrameWindowFeature picker, string title, string? initial)
    {
        var results = picker.ShowOpenFolder(title, defaultPath: ExistingDialogDirectory(initial));
        return results.FirstOrDefault(p => p is not null);
    }
}
