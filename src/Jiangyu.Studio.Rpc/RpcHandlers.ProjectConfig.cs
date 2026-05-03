using System.Text.Json;
using Jiangyu.Core.Config;
using Jiangyu.Shared;
using static Jiangyu.Studio.Rpc.RpcHelpers;

namespace Jiangyu.Studio.Rpc;

public static partial class RpcHandlers
{
    [McpTool("jiangyu_read_manifest",
        "Read the project configuration (jiangyu.json manifest). Returns the parsed ProjectConfig object with mod name, version, author, dependencies, etc.")]
    [McpParam("projectPath", "string", "Absolute path to the project root directory.", Required = true)]
    internal static JsonElement GetProjectConfig(JsonElement? parameters)
    {
        var projectPath = RequireString(parameters, "projectPath");
        if (!Directory.Exists(projectPath))
            throw new ArgumentException($"Project directory not found: {projectPath}");

        var config = ProjectConfig.Load(projectPath);
        return JsonSerializer.SerializeToElement(config);
    }

    /// <summary>
    /// Sets / clears the per-project asset export path. Not exposed via MCP
    /// — the asset browser UI is the only caller — but it lives here so the
    /// MCP binary can still see the project-config write path if a future
    /// tool needs it.
    /// </summary>
    public static JsonElement SetProjectAssetExportPath(JsonElement? parameters)
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
