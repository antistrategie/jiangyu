using System.CommandLine;
using Jiangyu.Core.Config;

namespace Jiangyu.Cli.Commands;

public static class InitCommand
{
    public static Command Create()
    {
        var command = new Command("init", "Scaffold a new mod project");
        command.SetAction(async (parseResult) =>
        {
            var projectDir = Directory.GetCurrentDirectory();

            try
            {
                var dirName = await ProjectScaffold.InitAsync(projectDir);
                Console.WriteLine($"Initialised mod project: {dirName}");
                return 0;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
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
