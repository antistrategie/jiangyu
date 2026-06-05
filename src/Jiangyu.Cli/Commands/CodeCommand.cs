using System.CommandLine;
using Jiangyu.Core.Code;
using Jiangyu.Core.Config;

namespace Jiangyu.Cli.Commands;

public static class CodeCommand
{
    public static Command Create()
    {
        var command = new Command("code", "Manage the per-mod C# project (code/)");
        command.Add(CreateSync());
        return command;
    }

    private static Command CreateSync()
    {
        var command = new Command("sync", "Scaffold or refresh code/ for C# modding. Idempotent: creates the project on first run, refreshes Jiangyu-managed build files and local.props on re-run, preserves your .csproj and source.");
        command.SetAction((parseResult) =>
        {
            var projectDir = Directory.GetCurrentDirectory();

            try
            {
                var log = new ConsoleLogSink();
                var config = GlobalConfig.Load();

                var (gameDir, gameError) = GlobalConfig.ResolveGamePath(config);
                if (gameDir is null)
                    log.Warning($"game path unresolved ({gameError}); local.props will omit GameDir.");

                var (sdkDir, sdkError) = GlobalConfig.ResolveSdkDir(config);
                if (sdkDir is null)
                    log.Warning($"SDK path unresolved ({sdkError}); local.props will omit JiangyuSdkDir.");

                var result = new CodeProjectScaffolder(log).Init(projectDir, gameDir, sdkDir);

                Console.WriteLine();
                Console.WriteLine($"Created    {result.CreatedFiles.Count} file(s)");
                Console.WriteLine($"Updated    {result.OverwrittenFiles.Count} file(s)");
                Console.WriteLine($"Preserved  {result.PreservedFiles.Count} file(s)");
                Console.WriteLine();
                Console.WriteLine("Next steps:");
                Console.WriteLine("  1. Author your mod under code/ (each feature is a JiangyuSystem subclass).");
                Console.WriteLine("  2. Run 'jiangyu compile' to build it and package the DLL alongside the manifest.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: code sync failed: {ex.Message}");
                return 1;
            }
        });
        return command;
    }
}
