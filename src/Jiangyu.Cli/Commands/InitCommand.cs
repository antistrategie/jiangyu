using System.CommandLine;
using Jiangyu.Core.Models;

namespace Jiangyu.Cli.Commands;

public static class InitCommand
{
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
                return;
            }

            var dirName = Path.GetFileName(projectDir) ?? "MyMod";
            var manifest = ModManifest.CreateDefault(dirName);

            foreach (var dir in new[] { "models", "textures", "audio", "templates", "compiled" })
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
        });
        return command;
    }
}
