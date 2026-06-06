using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Config;
using Jiangyu.Core.Deploy;
using Jiangyu.Core.Rpc;
using static Jiangyu.Studio.Rpc.RpcHelpers;

namespace Jiangyu.Studio.Rpc;

public static partial class RpcHandlers
{
    [McpTool("jiangyu_loader_deploy",
        "Deploy a loader build into the game's Mods/ folder. 'variant' is 'user' (lean) or 'dev' (Studio bridge + diagnostics); omit to redeploy the currently deployed variant (an update). Returns {variant, version, destDir}.")]
    internal static JsonElement DeployLoader(JsonElement? parameters)
    {
        var config = GlobalConfig.Load();

        var (gameDir, gameError) = GlobalConfig.ResolveGamePath(config);
        if (gameDir is null)
            throw new InvalidOperationException(gameError ?? "game path not resolved.");

        // Omitting variant redeploys whatever is already in Mods/ (an update),
        // falling back to the lean user build on a fresh install.
        var variant = TryGetString(parameters, "variant")
            ?? LoaderVariantDetector.DetectDeployed(gameDir)
            ?? "user";

        var (loaderDll, resolveError) = GlobalConfig.ResolveLoaderDll(config, variant);
        if (loaderDll is null)
            throw new InvalidOperationException(resolveError ?? $"could not resolve the {variant} loader.");

        try
        {
            LoaderDeployer.Deploy(loaderDll, gameDir);
        }
        catch (IOException ex)
        {
            // On Windows the running game holds Mods/Jiangyu.Loader.dll open.
            throw new InvalidOperationException(
                $"could not replace the deployed loader. Close MENACE if it is running, then try again. ({ex.Message})");
        }

        // Report the now-deployed state read back from the DLL, not the requested input.
        var deployed = LoaderVariantDetector.InspectDeployed(gameDir);
        return JsonSerializer.SerializeToElement(new LoaderDeployResult
        {
            Variant = deployed.Variant ?? variant,
            Version = deployed.Version,
        });
    }

    [RpcType]
    internal sealed class LoaderDeployResult
    {
        [JsonPropertyName("variant")]
        public required string Variant { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }
}
