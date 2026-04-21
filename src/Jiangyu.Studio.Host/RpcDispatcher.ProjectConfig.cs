using System.Text.Json;
using InfiniFrame;
using Jiangyu.Core.Config;

namespace Jiangyu.Studio.Host;

public static partial class RpcDispatcher
{
    private static JsonElement HandleGetProjectConfig(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var projectPath = RequireString(parameters, "projectPath");
        if (!Directory.Exists(projectPath))
            throw new ArgumentException($"Project directory not found: {projectPath}");

        var config = ProjectConfig.Load(projectPath);
        return JsonSerializer.SerializeToElement(config);
    }

    private static JsonElement HandleSetProjectAssetExportPath(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var projectPath = RequireString(parameters, "projectPath");
        var exportPath = TryGetString(parameters, "exportPath");

        if (!Directory.Exists(projectPath))
            throw new ArgumentException($"Project directory not found: {projectPath}");

        var config = ProjectConfig.Load(projectPath);
        config.AssetExportPath = string.IsNullOrWhiteSpace(exportPath) ? null : exportPath;
        config.Save(projectPath);

        return JsonSerializer.SerializeToElement(config);
    }
}
