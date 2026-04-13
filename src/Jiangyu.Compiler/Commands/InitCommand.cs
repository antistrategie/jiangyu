using Jiangyu.Compiler.Models;

namespace Jiangyu.Compiler.Commands;

public static class InitCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var projectDir = Directory.GetCurrentDirectory();

        // Check if already initialised
        var manifestPath = Path.Combine(projectDir, ModManifest.FileName);
        if (File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"Error: {ModManifest.FileName} already exists in this directory.");
            return 1;
        }

        // Use directory name as default mod name
        var dirName = Path.GetFileName(projectDir) ?? "MyMod";

        var manifest = ModManifest.CreateDefault(dirName);

        // Create directory structure
        var directories = new[] { "models", "textures", "audio", "templates", "compiled" };
        foreach (var dir in directories)
        {
            Directory.CreateDirectory(Path.Combine(projectDir, dir));
        }

        // Create .jiangyu/ with default config
        Directory.CreateDirectory(Path.Combine(projectDir, ProjectConfig.ConfigDir));

        var config = new ProjectConfig();
        await File.WriteAllTextAsync(
            Path.Combine(projectDir, ProjectConfig.FilePath),
            config.ToJson());

        // Write jiangyu.json
        await File.WriteAllTextAsync(manifestPath, manifest.ToJson());

        // Write .gitignore
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
        Console.WriteLine("  jiangyu.json          Mod metadata");
        Console.WriteLine("  .jiangyu/config.json  Local config (game path, Unity editor)");
        Console.WriteLine("  models/               Model files (GLB, FBX)");
        Console.WriteLine("  textures/             Texture files");
        Console.WriteLine("  audio/                Audio files");
        Console.WriteLine("  templates/            Template patches (JSON)");
        Console.WriteLine("  compiled/             Build output");
        Console.WriteLine();
        Console.WriteLine("Next: edit .jiangyu/config.json to set your game and Unity editor paths.");

        return 0;
    }
}
