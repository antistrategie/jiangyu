using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniFrame;
using Jiangyu.Core.Deploy;
using Jiangyu.Core.Rpc;
using static Jiangyu.Studio.Rpc.RpcHelpers;

namespace Jiangyu.Studio.Host.Rpc;

public static partial class RpcDispatcher
{
    // Non-blocking package: zips the existing compiled/ tree on a worker thread under the
    // shared build gate and reports completion via the packageFinished notification. The
    // blocking RpcHandlers.Package path is kept for MCP, where a blocking tool call is
    // expected.
    private static JsonElement HandlePackage(IInfiniFrameWindow window, JsonElement? parameters)
    {
        var projectRoot = ProjectWatcher.ProjectRoot
            ?? throw new InvalidOperationException("No project open.");
        var outputDirectory = TryGetString(parameters, "outputDirectory");

        return StartBuildOp(
            "packageFinished",
            () =>
            {
                var result = ModPackager.PackProject(projectRoot, outputDirectory);
                return new PackageFinishedEvent
                {
                    Success = true,
                    ModName = result.ModName,
                    Version = result.Version,
                    ArchivePath = result.ArchivePath,
                };
            },
            ex => new PackageFinishedEvent { Success = false, ErrorMessage = ex.Message });
    }

    [RpcType]
    internal sealed class PackageFinishedEvent
    {
        [JsonPropertyName("success")]
        public required bool Success { get; set; }

        [JsonPropertyName("modName")]
        public string? ModName { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("archivePath")]
        public string? ArchivePath { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }
    }
}
