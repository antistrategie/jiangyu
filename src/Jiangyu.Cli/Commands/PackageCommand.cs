using System.CommandLine;
using Jiangyu.Core.Deploy;

namespace Jiangyu.Cli.Commands;

public static class PackageCommand
{
    public static Command Create()
    {
        var outputOption = new Option<string?>("--output")
        {
            Description = "Directory to write the archive into. Defaults to the project directory.",
        };

        var command = new Command("package", "Package the compiled mod into a distributable zip (run compile first)")
        {
            outputOption,
        };

        command.SetAction(parseResult =>
        {
            var projectDir = Directory.GetCurrentDirectory();
            try
            {
                var result = ModPackager.PackProject(projectDir, parseResult.GetValue(outputOption));
                Console.WriteLine($"Packaged {result.ModName} {result.Version} -> {result.ArchivePath}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        });

        return command;
    }
}
