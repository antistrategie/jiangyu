using System.CommandLine;
using Jiangyu.Core.Assets;

namespace Jiangyu.Cli.Commands;

public static class InspectPackageCommand
{
    public static Command Create()
    {
        var dirArg = new Argument<string>("directory") { Description = "Model package directory to validate" };

        var command = new Command("package", "Validate an exported model package (.gltf + textures)")
        {
            dirArg
        };
        command.SetAction((ctx) =>
        {
            var packageDir = ctx.GetRequiredValue(dirArg);

            if (!Directory.Exists(packageDir))
            {
                Console.Error.WriteLine($"Error: directory not found: {packageDir}");
                ctx.ExitCode = 1;
                return;
            }

            var result = PackageValidationService.Validate(packageDir);

            Console.WriteLine($"Package: {Path.GetFullPath(packageDir)}");
            Console.WriteLine();
            foreach (var line in result.Info)
                Console.WriteLine($"  {line}");

            if (!result.IsValid)
            {
                Console.WriteLine();
                Console.Error.WriteLine($"  {result.Issues.Count} issue(s):");
                foreach (var issue in result.Issues)
                    Console.Error.WriteLine($"    - {issue}");
                ctx.ExitCode = 1;
                return;
            }

            Console.WriteLine();
            Console.WriteLine("  Package OK");
            ctx.ExitCode = 0;
        });

        return command;
    }
}
