using System.CommandLine;
using Jiangyu.Core.Config;
using Jiangyu.Core.Deploy;
using Jiangyu.Core.Models;

namespace Jiangyu.Cli.Commands;

public static class DeployCommand
{
    public static Command Create()
    {
        var command = new Command("deploy", "Deploy the compiled mod into the game's Mods directory (clean: removes the existing deployed folder first)");
        command.SetAction(parseResult =>
        {
            var projectDir = Directory.GetCurrentDirectory();

            var manifestPath = Path.Combine(projectDir, ModManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                Console.Error.WriteLine($"Error: {ModManifest.FileName} not found. Run 'jiangyu init' first.");
                return 1;
            }

            var compiledDir = Path.Combine(projectDir, "compiled");
            if (!Directory.Exists(compiledDir))
            {
                Console.Error.WriteLine("Error: compiled/ not found. Run 'jiangyu compile' first.");
                return 1;
            }

            ModManifest manifest;
            try
            {
                manifest = ModManifest.FromJson(File.ReadAllText(manifestPath));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: could not read {ModManifest.FileName}: {ex.Message}");
                return 1;
            }

            var (gameDir, error) = GlobalConfig.ResolveGamePath(GlobalConfig.Load());
            if (gameDir is null)
            {
                Console.Error.WriteLine($"Error: {error}");
                return 1;
            }

            string dest;
            try
            {
                dest = ModDeployer.ResolveModDestination(gameDir, manifest.Name);
                ModDeployer.Deploy(compiledDir, dest);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: deploy failed: {ex.Message}");
                return 1;
            }

            Console.WriteLine($"Deployed {manifest.Name} -> {dest}");
            return 0;
        });
        return command;
    }
}
