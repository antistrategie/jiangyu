using System.CommandLine;
using Jiangyu.Core.Models;

namespace Jiangyu.Cli.Commands;

public static class InitCommand
{
    public static Command Create()
    {
        var command = new Command("init", "Scaffold a new mod project");
        command.SetAction(async (parseResult) =>
        {
            var projectDir = Directory.GetCurrentDirectory();

            var manifestPath = Path.Combine(projectDir, ModManifest.FileName);
            if (File.Exists(manifestPath))
            {
                Console.Error.WriteLine($"Error: {ModManifest.FileName} already exists in this directory.");
                return 1;
            }

            try
            {
                var dirName = Path.GetFileName(projectDir) ?? "MyMod";
                var manifest = ModManifest.CreateDefault(dirName);

                await File.WriteAllTextAsync(manifestPath, manifest.ToJson());

                var gitignorePath = Path.Combine(projectDir, ".gitignore");
                if (!File.Exists(gitignorePath))
                {
                    await File.WriteAllTextAsync(gitignorePath, """
                        .jiangyu/
                        compiled/
                        """);
                }

                Console.WriteLine($"Initialised mod project: {dirName}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: init failed: {ex.Message}");
                return 1;
            }
        });
        return command;
    }
}
