using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Config;
using Jiangyu.Core.Deploy;
using Jiangyu.Core.Models;
using Jiangyu.Core.Rpc;

namespace Jiangyu.Studio.Rpc;

public static partial class RpcHandlers
{
    [McpTool("jiangyu_deploy",
        "Deploy the compiled mod into the game's Mods/<name>/ folder. Clean: the existing deployed folder is removed first, so stale artifacts never linger. Requires compiled/ (run jiangyu_compile first). Returns {modName, destDir}.",
        LongRunning = true)]
    internal static JsonElement Deploy(JsonElement? __)
    {
        var projectRoot = RpcContext.ProjectRoot ?? throw new InvalidOperationException("No project open.");

        // Share the build gate so deploy can't race a concurrent compile rewriting compiled/.
        BeginBuildOp();
        try
        {
            var (modName, destDir) = DeployCore(projectRoot);
            return JsonSerializer.SerializeToElement(new DeployResult { ModName = modName, DestDir = destDir });
        }
        finally
        {
            EndBuildOp();
        }
    }

    /// <summary>The deploy work without the build gate, shared by the MCP entry and Studio's
    /// non-blocking dispatcher handler. Copies the project's compiled/ output into the game's
    /// <c>Mods/&lt;name&gt;/</c> folder and returns the mod name and destination.</summary>
    internal static (string ModName, string DestDir) DeployCore(string projectRoot)
    {
        var manifestPath = Path.Combine(projectRoot, ModManifest.FileName);
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"{ModManifest.FileName} not found. Open a Jiangyu project first.");

        var compiledDir = Path.Combine(projectRoot, "compiled");
        if (!Directory.Exists(compiledDir))
            throw new InvalidOperationException("compiled/ not found. Run compile first.");

        ModManifest manifest;
        try { manifest = ModManifest.FromJson(File.ReadAllText(manifestPath)); }
        catch (Exception ex) { throw new InvalidOperationException($"could not read {ModManifest.FileName}: {ex.Message}"); }

        var (gameDir, error) = GlobalConfig.ResolveGamePath(GlobalConfig.Load());
        if (gameDir is null)
            throw new InvalidOperationException(error ?? "game path not resolved.");

        // ResolveModDestination rejects a mod name that could escape Mods/, which the
        // deploy clears with a recursive delete.
        var dest = ModDeployer.ResolveModDestination(gameDir, manifest.Name);
        ModDeployer.Deploy(compiledDir, dest);
        return (manifest.Name, dest);
    }

    [RpcType]
    internal sealed class DeployResult
    {
        [JsonPropertyName("modName")]
        public required string ModName { get; set; }

        [JsonPropertyName("destDir")]
        public required string DestDir { get; set; }
    }
}
