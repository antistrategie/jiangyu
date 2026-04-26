using System.Text.Json;
using InfiniFrame;
using Jiangyu.Core.Config;

namespace Jiangyu.Studio.Host;

public static partial class RpcDispatcher
{
    /// <summary>
    /// Returns all Studio settings as a JSON object. The frontend mirrors
    /// this into localStorage for fast synchronous reads on subsequent
    /// launches, but the filesystem is the source of truth.
    /// </summary>
    private static JsonElement HandleGetStudioSettings(IInfiniFrameWindow _, JsonElement? __)
    {
        var settings = StudioSettings.Load();
        return JsonSerializer.SerializeToElement(settings);
    }

    /// <summary>
    /// Updates a single Studio setting. Parameters: {key, value}.
    /// The key must match a JSON property name on <see cref="StudioSettings"/>.
    /// Returns the full settings object after the update.
    /// </summary>
    private static JsonElement HandleSetStudioSetting(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var key = RequireString(parameters, "key");
        var valueElement = parameters is { } p && p.TryGetProperty("value", out var v)
            ? v
            : throw new ArgumentException("Missing 'value' parameter");

        var settings = StudioSettings.Load();

        switch (key)
        {
            case "editorFontSize":
                var fontSize = valueElement.GetInt32();
                settings.EditorFontSize = Math.Clamp(
                    fontSize,
                    StudioSettings.EditorFontSizeMin,
                    StudioSettings.EditorFontSizeMax);
                break;
            case "uiFontScale":
                var uiScale = valueElement.GetInt32();
                settings.UiFontScale = Math.Clamp(
                    uiScale,
                    StudioSettings.UiFontScaleMin,
                    StudioSettings.UiFontScaleMax);
                break;
            case "editorWordWrap":
                var ww = valueElement.GetString() ?? "on";
                settings.EditorWordWrap = ww is "off" ? "off" : "on";
                break;
            case "sidebarHidden":
                settings.SidebarHidden = valueElement.ValueKind == JsonValueKind.True;
                break;
            case "sessionRestoreProject":
                settings.SessionRestoreProject = valueElement.ValueKind == JsonValueKind.True;
                break;
            case "sessionRestoreTabs":
                settings.SessionRestoreTabs = valueElement.ValueKind == JsonValueKind.True;
                break;
            case "editorKeybindMode":
                var km = valueElement.GetString() ?? "default";
                settings.EditorKeybindMode = km is "vim" ? "vim" : "default";
                break;
            case "templateEditorMode":
                var tm = valueElement.GetString() ?? "visual";
                settings.TemplateEditorMode = tm is "source" ? "source" : "visual";
                break;
            default:
                throw new ArgumentException($"Unknown Studio setting key: {key}");
        }

        settings.Save();
        return JsonSerializer.SerializeToElement(settings);
    }
}
