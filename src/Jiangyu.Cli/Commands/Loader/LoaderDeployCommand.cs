using System.CommandLine;
using Jiangyu.Core.Config;
using Jiangyu.Core.Deploy;

namespace Jiangyu.Cli.Commands.Loader;

public static class LoaderDeployCommand
{
    public static Command Create()
    {
        var variantOption = new Option<string?>("--variant")
        {
            Description = "Loader build to deploy: 'user' (lean) or 'dev' (Studio bridge + diagnostics). Omit to redeploy the currently deployed variant (an update), or 'user' when none is deployed.",
        };

        var command = new Command("deploy", "Deploy a loader build into the game's Mods/ folder (overwrites Mods/Jiangyu.Loader.dll)")
        {
            variantOption,
        };

        command.SetAction(parseResult =>
        {
            var config = GlobalConfig.Load();

            var (gameDir, gameError) = GlobalConfig.ResolveGamePath(config);
            if (gameDir is null)
            {
                Console.Error.WriteLine($"Error: {gameError}");
                return 1;
            }

            // Omitting --variant redeploys whatever is already in Mods/ (an update),
            // falling back to the lean user build on a fresh install.
            var variant = parseResult.GetValue(variantOption)
                ?? LoaderVariantDetector.DetectDeployed(gameDir)
                ?? "user";

            var (loaderDll, resolveError) = GlobalConfig.ResolveLoaderDll(config, variant);
            if (loaderDll is null)
            {
                Console.Error.WriteLine($"Error: {resolveError}");
                return 1;
            }

            string dest;
            try
            {
                dest = LoaderDeployer.Deploy(loaderDll, gameDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: loader deploy failed: {ex.Message}");
                return 1;
            }

            Console.WriteLine($"Deployed {variant} loader -> {dest}");
            return 0;
        });

        return command;
    }
}
