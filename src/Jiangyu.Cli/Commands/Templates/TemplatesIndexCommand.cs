using System.CommandLine;
using Jiangyu.Core.Config;
using Jiangyu.Core.Il2Cpp;
using Jiangyu.Core.Models;
using Jiangyu.Core.Unity;

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

            var ctx = resolution.Context!;
            var log = new ConsoleLogSink();
            var service = ctx.CreateTemplateIndexService(new ConsoleProgressSink(), log);
            try
            {
                service.BuildIndex();

                // The supplement is refreshed after the template index: AssetRipper
                // initialises LibCpp2IL first, then Cpp2IL registers its instruction
                // sets idempotently.
                Il2CppMetadataCache.BuildIfStale(
                    ctx.CachePath,
                    ctx.GameDataPath,
                    () => UnityVersionValidationService.DetectGameVersion(ctx.GameDataPath),
                    log);

                TemplateIndex? index = service.LoadIndex();
                TemplateIndexManifest? manifest = service.LoadManifest();

                Console.WriteLine($"Indexed {manifest?.InstanceCount ?? index?.Instances.Count ?? 0} template instances across {manifest?.TemplateTypeCount ?? index?.TemplateTypes.Count ?? 0} template types.");
                if (manifest is not null)
                {
                    Console.WriteLine($"Classification: {manifest.RuleVersion} ({manifest.RuleDescription})");
                    if (manifest.SkippedValueCount > 0)
                        Console.WriteLine($"Skipped values for {manifest.SkippedValueCount} template(s).");
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
