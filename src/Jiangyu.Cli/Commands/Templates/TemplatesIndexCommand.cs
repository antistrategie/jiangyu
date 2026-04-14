using System.CommandLine;
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
            var resolution = EnvironmentContext.ResolveFromGlobalConfig();
            if (!resolution.Success)
            {
                Console.Error.WriteLine(resolution.Error);
                return 1;
            }

            var service = resolution.Context!.CreateTemplateIndexService(new ConsoleProgressSink(), new ConsoleLogSink());
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
}
