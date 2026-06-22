using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Deploy;
using Jiangyu.Core.Rpc;
using static Jiangyu.Studio.Rpc.RpcHelpers;

namespace Jiangyu.Studio.Rpc;

public static partial class RpcHandlers
{
    [McpTool("jiangyu_package",
        "Package the compiled mod into a distributable <name>-<version>.zip. Works on the existing compiled/ output and does NOT compile, so run jiangyu_compile first (fails if compiled/ is absent). 'outputDirectory' is where to write the archive (defaults to the project directory). Returns {modName, version, archivePath}.",
        LongRunning = true)]
    internal static JsonElement Package(JsonElement? parameters)
    {
        var projectRoot = RpcContext.ProjectRoot ?? throw new InvalidOperationException("No project open.");
        var outputDirectory = TryGetString(parameters, "outputDirectory");

        // Share the build gate so an agent packaging can't race a concurrent compile wiping
        // compiled/ out from under the zip.
        BeginBuildOp();
        try
        {
            var result = ModPackager.PackProject(projectRoot, outputDirectory);
            return JsonSerializer.SerializeToElement(new PackageResult
            {
                ModName = result.ModName,
                Version = result.Version,
                ArchivePath = result.ArchivePath,
            });
        }
        finally
        {
            EndBuildOp();
        }
    }

    [RpcType]
    internal sealed class PackageResult
    {
        [JsonPropertyName("modName")]
        public required string ModName { get; set; }

        [JsonPropertyName("version")]
        public required string Version { get; set; }

        [JsonPropertyName("archivePath")]
        public required string ArchivePath { get; set; }
    }
}
