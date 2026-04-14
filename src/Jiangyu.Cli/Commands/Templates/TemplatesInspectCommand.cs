using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;

namespace Jiangyu.Cli.Commands.Templates;

public static class TemplatesInspectCommand
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Command Create()
    {
        var typeOption = new Option<string?>("--type") { Description = "Template class name (for name-based resolution)" };
        var nameOption = new Option<string?>("--name") { Description = "Template name to resolve through the template index" };
        var collectionOption = new Option<string?>("--collection") { Description = "Asset collection name (stable identity)" };
        var pathIdOption = new Option<long?>("--path-id") { Description = "Asset path ID (stable identity)" };
        var maxDepthOption = new Option<int>("--max-depth") { Description = "Maximum nested depth to include", DefaultValueFactory = _ => 4 };
        var maxArraySampleOption = new Option<int>("--max-array-sample") { Description = "Maximum number of array elements to include", DefaultValueFactory = _ => 8 };
        var outputOption = new Option<string>("--output") { Description = "JSON output mode: json or pretty", DefaultValueFactory = _ => "pretty" };

        var command = new Command("inspect", "Inspect one template instance via the template index or stable identity")
        {
            typeOption,
            nameOption,
            collectionOption,
            pathIdOption,
            maxDepthOption,
            maxArraySampleOption,
            outputOption,
        };

        command.SetAction((parseResult) =>
        {
            string? className = parseResult.GetValue(typeOption);
            string? name = parseResult.GetValue(nameOption);
            string? collection = parseResult.GetValue(collectionOption);
            long? pathId = parseResult.GetValue(pathIdOption);
            int maxDepth = parseResult.GetValue(maxDepthOption);
            int maxArraySampleLength = parseResult.GetValue(maxArraySampleOption);
            string output = (parseResult.GetValue(outputOption) ?? "pretty").ToLowerInvariant();

            if (!TryValidateInput(className, name, collection, pathId, output, maxDepth, maxArraySampleLength, out string? error))
            {
                Console.Error.WriteLine($"Error: {error}");
                return 1;
            }

            var (gameDataPath, gameDataError) = GlobalConfig.ResolveGameDataPath();
            if (gameDataPath is null)
            {
                Console.Error.WriteLine(gameDataError);
                return 1;
            }

            string cachePath = GlobalConfig.Load().GetCachePath();
            var objectInspectionService = new ObjectInspectionService(gameDataPath, cachePath, new ConsoleProgressSink(), new ConsoleLogSink());

            var request = new ObjectInspectionRequest
            {
                Collection = collection,
                PathId = pathId,
                Name = name,
                ClassName = className,
                MaxDepth = maxDepth,
                MaxArraySampleLength = maxArraySampleLength,
            };

            try
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var templateIndexService = new TemplateIndexService(gameDataPath, cachePath, new ConsoleProgressSink(), new ConsoleLogSink());
                    var resolver = new TemplateResolver(templateIndexService.LoadIndex());
                    TemplateResolutionResult resolution = resolver.Resolve(className!, name);

                    switch (resolution.Status)
                    {
                        case TemplateResolutionStatus.Success:
                            request = new ObjectInspectionRequest
                            {
                                Collection = resolution.Resolved!.Identity.Collection,
                                PathId = resolution.Resolved.Identity.PathId,
                                MaxDepth = maxDepth,
                                MaxArraySampleLength = maxArraySampleLength,
                            };
                            break;
                        case TemplateResolutionStatus.IndexUnavailable:
                            Console.Error.WriteLine("Error: template index not found. Run 'jiangyu templates index' first.");
                            return 1;
                        case TemplateResolutionStatus.NotFound:
                            Console.Error.WriteLine($"Error: no template named '{name}' found for type '{className}'.");
                            return 1;
                        case TemplateResolutionStatus.Ambiguous:
                            Console.Error.WriteLine($"Error: template name '{name}' is ambiguous for type '{className}'.");
                            foreach (ResolvedTemplateCandidate candidate in resolution.Candidates)
                            {
                                Console.Error.WriteLine($"  {candidate.Name} ({candidate.ClassName}) in {candidate.Identity.Collection} [pathId={candidate.Identity.PathId}]");
                            }
                            Console.Error.WriteLine("Rerun with --collection and --path-id.");
                            return 1;
                        default:
                            Console.Error.WriteLine($"Error: unsupported resolution status '{resolution.Status}'.");
                            return 1;
                    }
                }

                ObjectInspectionResult result = objectInspectionService.Inspect(request);
                string json = JsonSerializer.Serialize(result, output == "json" ? CompactJsonOptions : PrettyJsonOptions);
                Console.WriteLine(json);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: template inspect failed: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    private static bool TryValidateInput(
        string? className,
        string? name,
        string? collection,
        long? pathId,
        string output,
        int maxDepth,
        int maxArraySampleLength,
        out string? error)
    {
        bool hasNameMode = !string.IsNullOrWhiteSpace(className) || !string.IsNullOrWhiteSpace(name);
        bool hasIdentityMode = !string.IsNullOrWhiteSpace(collection) || pathId.HasValue;
        bool hasPartialNameMode = !string.IsNullOrWhiteSpace(className) ^ !string.IsNullOrWhiteSpace(name);
        bool hasPartialIdentity = !string.IsNullOrWhiteSpace(collection) ^ pathId.HasValue;

        if (hasPartialNameMode)
        {
            error = "use both --type and --name together.";
            return false;
        }

        if (hasPartialIdentity)
        {
            error = "use both --collection and --path-id together.";
            return false;
        }

        if (hasNameMode == hasIdentityMode)
        {
            error = "use either --type + --name or --collection + --path-id.";
            return false;
        }

        if (!string.Equals(output, "json", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(output, "pretty", StringComparison.OrdinalIgnoreCase))
        {
            error = "output must be 'json' or 'pretty'.";
            return false;
        }

        if (maxDepth < 1)
        {
            error = "--max-depth must be at least 1.";
            return false;
        }

        if (maxArraySampleLength < 0)
        {
            error = "--max-array-sample must be 0 or greater.";
            return false;
        }

        error = null;
        return true;
    }
}
