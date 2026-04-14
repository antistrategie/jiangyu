using System.CommandLine;
using Jiangyu.Core.Models;

namespace Jiangyu.Cli.Commands;

public static class InitCommand
{
    private static readonly string[] stringArray = ["models", "textures", "audio", "templates", "compiled"];

    public static Command Create()
    {
        var command = new Command("init", "Scaffold a new mod project");
        command.SetAction(async (ctx) =>
        {
            var projectDir = Directory.GetCurrentDirectory();

            var manifestPath = Path.Combine(projectDir, ModManifest.FileName);
            if (File.Exists(manifestPath))
            {
                Console.Error.WriteLine($"Error: {ModManifest.FileName} already exists in this directory.");
                ctx.ExitCode = 1;
                return;
            }

            try
            {
                var dirName = Path.GetFileName(projectDir) ?? "MyMod";
                var manifest = ModManifest.CreateDefault(dirName);

                foreach (var dir in stringArray)
                    Directory.CreateDirectory(Path.Combine(projectDir, dir));

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
                Console.WriteLine();
                Console.WriteLine("  jiangyu.json          Mod manifest");
                Console.WriteLine("  models/               Model files (glTF, GLB)");
                Console.WriteLine("  textures/             Texture files");
                Console.WriteLine("  audio/                Audio files");
                Console.WriteLine("  templates/            Template patches (JSON)");
                Console.WriteLine("  compiled/             Build output");
                ctx.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: init failed: {ex.Message}");
                ctx.ExitCode = 1;
            }
        });
        return command;
    }
}
