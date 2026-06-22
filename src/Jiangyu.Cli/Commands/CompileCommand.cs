using System.CommandLine;
using Jiangyu.Core.Compile;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;

namespace Jiangyu.Cli.Commands;

public static class CompileCommand
{
    public static Command Create()
    {
        var cleanOption = new Option<bool>("--clean")
        {
            Description = "Force a full rebuild, ignoring the incremental build cache.",
        };

        var command = new Command("compile", "Compile mod assets into AssetBundles")
        {
            cleanOption,
        };
        command.SetAction(async (parseResult) =>
        {
            var projectDir = Directory.GetCurrentDirectory();

            var manifestPath = Path.Combine(projectDir, ModManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                Console.Error.WriteLine($"Error: {ModManifest.FileName} not found. Run 'jiangyu init' first.");
                return 1;
            }

            try
            {
                var manifest = ModManifest.FromJson(await File.ReadAllTextAsync(manifestPath));
                var config = GlobalConfig.Load();

                var service = new CompilationService(new ConsoleLogSink(), new ConsoleProgressSink());
                CompilationResult result = await service.CompileAsync(new CompilationInput
                {
                    Manifest = manifest,
                    Config = config,
                    ProjectDirectory = projectDir,
                    Clean = parseResult.GetValue(cleanOption),
                });

                return result.Success ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: compile failed: {ex.Message}");
                return 1;
            }
        });
        return command;
    }
}
