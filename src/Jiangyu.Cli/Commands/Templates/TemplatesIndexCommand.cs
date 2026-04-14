using System.CommandLine;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;

namespace Jiangyu.Cli.Commands.Templates;

public static class TemplatesIndexCommand
{
    public static Command Create()
    {
        var command = new Command("index", "Build searchable template index from game data");
        command.SetAction((parseResult) =>
        {
            var (service, error) = CreateService();
            if (service is null)
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            try
            {
                service.BuildIndex();

                TemplateIndex? index = service.LoadIndex();
                TemplateIndexManifest? manifest = service.LoadManifest();

                Console.WriteLine($"Indexed {manifest?.InstanceCount ?? index?.Instances.Count ?? 0} template instances across {manifest?.TemplateTypeCount ?? index?.TemplateTypes.Count ?? 0} template types.");
                if (manifest is not null)
                {
                    Console.WriteLine($"Classification: {manifest.RuleVersion} ({manifest.RuleDescription})");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: template index failed: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    private static (TemplateIndexService? service, string? error) CreateService()
    {
        var (gameDataPath, error) = GlobalConfig.ResolveGameDataPath();
        if (gameDataPath is null)
        {
            return (null, error);
        }

        string cachePath = GlobalConfig.Load().GetCachePath();
        return (new TemplateIndexService(gameDataPath, cachePath, new ConsoleProgressSink(), new ConsoleLogSink()), null);
    }
}
