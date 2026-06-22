using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniFrame;
using Jiangyu.Core.Rpc;
using Jiangyu.Studio.Rpc;

namespace Jiangyu.Studio.Host.Rpc;

public static partial class RpcDispatcher
{
    // Non-blocking deploy: copies the existing compiled/ tree into the game's Mods/ folder on
    // a worker thread under the shared build gate (a recursive copy can take a beat for asset
    // mods, and the dispatcher runs handlers on the WebView thread). Reports completion via
    // the deployFinished notification. The blocking RpcHandlers.Deploy path is kept for MCP.
    private static JsonElement HandleDeploy(IInfiniFrameWindow window, JsonElement? parameters)
    {
        var projectRoot = ProjectWatcher.ProjectRoot
            ?? throw new InvalidOperationException("No project open.");

        return StartBuildOp(
            "deployFinished",
            () =>
            {
                var (modName, destDir) = RpcHandlers.DeployCore(projectRoot);
                return new DeployFinishedEvent { Success = true, ModName = modName, DestDir = destDir };
            },
            ex => new DeployFinishedEvent { Success = false, ErrorMessage = ex.Message });
    }

    [RpcType]
    internal sealed class DeployFinishedEvent
    {
        [JsonPropertyName("success")]
        public required bool Success { get; set; }

        [JsonPropertyName("modName")]
        public string? ModName { get; set; }

        [JsonPropertyName("destDir")]
        public string? DestDir { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }
    }
}
