using System.Text.Json;
using InfiniFrame;
using Jiangyu.Core.Config;
using Jiangyu.Shared;

namespace Jiangyu.Studio.Host.Rpc;

public static partial class RpcDispatcher
{
    [McpTool("jiangyu_read_manifest",
        "Read the project configuration (jiangyu.json manifest). Params: {\"projectPath\": \"/absolute/project/root\"} (required). Returns the parsed ProjectConfig object with mod name, version, author, dependencies, etc.")]
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
