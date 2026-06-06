using System.CommandLine;
using Jiangyu.Core.Config;
using Jiangyu.Core.Deploy;

namespace Jiangyu.Cli.Commands.Loader;

public static class LoaderStatusCommand
{
    public static Command Create()
    {
        var command = new Command("status", "Show which loader build is deployed in the game's Mods/ folder");
        command.SetAction(parseResult =>
        {
            var (gameDir, gameError) = GlobalConfig.ResolveGamePath(GlobalConfig.Load());
            if (gameDir is null)
            {
                Console.Error.WriteLine($"Error: {gameError}");
                return 1;
            }

            var loader = LoaderVariantDetector.InspectDeployed(gameDir);
            if (loader.Variant is null)
            {
                Console.WriteLine("No loader deployed.");
                return 0;
            }

            Console.WriteLine($"Deployed loader: {loader.Variant} (v{loader.Version ?? "unknown"})");
            return 0;
        });
        return command;
    }
}
