using System.CommandLine;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;

namespace Jiangyu.Cli.Commands.Templates;

public static class TemplatesSearchCommand
{
    public static Command Create()
    {
        var queryArgument = new Argument<string>("query")
        {
            Description = "Case-insensitive substring to match against type names, template IDs, and collections",
        };
        var typeOption = new Option<string?>("--type")
        {
            Description = "Restrict instance matches to one template class name",
        };

        var command = new Command("search", "Search template types and indexed instances by substring")
        {
            queryArgument,
            typeOption,
        };

        command.SetAction(parseResult =>
        {
            string query = parseResult.GetValue(queryArgument)!;
            string? className = parseResult.GetValue(typeOption);

            var resolution = EnvironmentContext.ResolveFromGlobalConfig();
            if (!resolution.Success)
            {
                Console.Error.WriteLine(resolution.Error);
                return 1;
            }

            var service = resolution.Context!.CreateTemplateIndexService(new ConsoleProgressSink(), new ConsoleLogSink());
            CachedIndexStatus indexStatus = service.GetIndexStatus();
            if (!indexStatus.IsCurrent)
            {
                Console.Error.WriteLine($"Error: {indexStatus.Reason}");
                return 1;
            }

            TemplateIndex? index = service.LoadIndex();
            var search = new TemplateSearchService(index).Search(query, className);

            if (search.Status == TemplateSearchStatus.IndexUnavailable)
            {
                Console.Error.WriteLine("Error: template index not found. Run 'jiangyu templates index' first.");
                return 1;
            }

            if (search.Status == TemplateSearchStatus.NotFound)
            {
                string scope = string.IsNullOrWhiteSpace(className)
                    ? "type, template ID, or collection"
                    : $"template ID or collection for type '{className}'";
                Console.Error.WriteLine($"Error: no templates matched '{query}' by {scope}.");
                return 1;
            }

            Print(search, className);
            return 0;
        });

        return command;
    }

    private static void Print(TemplateSearchResult result, string? className)
    {
        if (string.IsNullOrWhiteSpace(className) && result.MatchingTypes.Count > 0)
        {
            Console.WriteLine("Matching types:");
            int width = result.MatchingTypes.Max(entry => entry.ClassName.Length);
            foreach (TemplateTypeEntry entry in result.MatchingTypes)
            {
                string suffix = entry.TemplateAncestor is not null
                    ? $"  (via {entry.TemplateAncestor})"
                    : "";
                Console.WriteLine($"  {entry.ClassName.PadRight(width)}  {entry.Count}{suffix}");
            }

            if (result.MatchingInstances.Count > 0)
            {
                Console.WriteLine();
            }
        }

        if (result.MatchingInstances.Count == 0)
        {
            return;
        }

        Console.WriteLine("Matching instances:");
        int nameWidth = result.MatchingInstances.Max(instance => instance.Name.Length);
        int typeWidth = result.MatchingInstances.Max(instance => instance.ClassName.Length);

        foreach (TemplateInstanceEntry instance in result.MatchingInstances)
        {
            Console.WriteLine(
                $"  {instance.Name.PadRight(nameWidth)}  {instance.ClassName.PadRight(typeWidth)}  {instance.Identity.Collection} [pathId={instance.Identity.PathId}]");
        }
    }
}
