using System.CommandLine;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;

namespace Jiangyu.Cli.Commands.Templates;

public static class TemplatesListCommand
{
    public static Command Create()
    {
        var typeOption = new Option<string?>("--type") { Description = "List instances for one template class name" };

        var command = new Command("list", "List indexed template types or instances")
        {
            typeOption,
        };

        command.SetAction((parseResult) =>
        {
            string? className = parseResult.GetValue(typeOption);

            var (service, error) = CreateService();
            if (service is null)
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            if (!service.IsIndexCurrent())
            {
                Console.Error.WriteLine("Error: template index is missing or stale for the current game version. Run 'jiangyu templates index' first.");
                return 1;
            }

            TemplateIndex? index = service.LoadIndex();
            if (index is null)
            {
                Console.Error.WriteLine("Error: template index not found. Run 'jiangyu templates index' first.");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(className))
            {
                PrintTemplateTypes(index);
                return 0;
            }

            return PrintTemplateInstances(index, className) ? 0 : 1;
        });

        return command;
    }

    private static void PrintTemplateTypes(TemplateIndex index)
    {
        int width = index.TemplateTypes.Count == 0
            ? 0
            : index.TemplateTypes.Max(entry => entry.ClassName.Length);

        foreach (TemplateTypeEntry entry in index.TemplateTypes)
        {
            Console.WriteLine($"{entry.ClassName.PadRight(width)}  {entry.Count}");
        }
    }

    private static bool PrintTemplateInstances(TemplateIndex index, string className)
    {
        var instances = index.Instances
            .Where(instance => string.Equals(instance.ClassName, className, StringComparison.OrdinalIgnoreCase))
            .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(instance => instance.Identity.Collection, StringComparer.OrdinalIgnoreCase)
            .ThenBy(instance => instance.Identity.PathId)
            .ToList();

        if (instances.Count == 0)
        {
            Console.Error.WriteLine($"Error: no indexed template instances found for type '{className}'.");
            return false;
        }

        int nameWidth = instances.Max(instance => instance.Name.Length);
        foreach (TemplateInstanceEntry instance in instances)
        {
            Console.WriteLine($"{instance.Name.PadRight(nameWidth)}  {instance.ClassName}  {instance.Identity.Collection} [pathId={instance.Identity.PathId}]");
        }

        return true;
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
