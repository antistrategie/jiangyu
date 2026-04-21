using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;

namespace Jiangyu.Cli.Commands.Assets;

public static class InspectObjectCommand
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
        var collectionOption = new Option<string?>("--collection") { Description = "Asset collection name (stable identity)" };
        var pathIdOption = new Option<long?>("--path-id") { Description = "Asset path ID (stable identity)" };
        var nameOption = new Option<string?>("--name") { Description = "Asset name to resolve through the index" };
        var typeOption = new Option<string?>("--type") { Description = "Optional class name filter for name resolution" };
        var maxDepthOption = new Option<int>("--max-depth") { Description = "Maximum nested depth to include", DefaultValueFactory = _ => 4 };
        var maxArraySampleOption = new Option<int>("--max-array-sample") { Description = "Maximum number of array elements to include", DefaultValueFactory = _ => 8 };
        var outputOption = new Option<string>("--output") { Description = "JSON output mode: json or pretty", DefaultValueFactory = _ => "pretty" };

        var command = new Command("object", "Inspect one game object and dump its observed field tree")
        {
            collectionOption,
            pathIdOption,
            nameOption,
            typeOption,
            maxDepthOption,
            maxArraySampleOption,
            outputOption,
        };

        command.SetAction((parseResult) =>
        {
            var collection = parseResult.GetValue(collectionOption);
            var pathId = parseResult.GetValue(pathIdOption);
            var name = parseResult.GetValue(nameOption);
            var className = parseResult.GetValue(typeOption);
            var maxDepth = parseResult.GetValue(maxDepthOption);
            var maxArraySampleLength = parseResult.GetValue(maxArraySampleOption);
            var output = (parseResult.GetValue(outputOption) ?? "pretty").ToLowerInvariant();

            if (!TryValidateInput(collection, pathId, name, output, maxDepth, maxArraySampleLength, out string? error))
            {
                Console.Error.WriteLine($"Error: {error}");
                return 1;
            }

            var resolution = EnvironmentContext.ResolveFromGlobalConfig();
            if (!resolution.Success)
            {
                Console.Error.WriteLine(resolution.Error);
                return 1;
            }

            var context = resolution.Context!;
            var service = context.CreateObjectInspectionService(new ConsoleProgressSink(), new ConsoleLogSink());

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
                ResolvedObjectCandidate? resolved = null;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var assetIndexService = context.CreateAssetPipelineService(new ConsoleProgressSink(), new ConsoleLogSink());
                    CachedIndexStatus indexStatus = assetIndexService.GetIndexStatus();
                    if (!indexStatus.IsCurrent)
                    {
                        Console.Error.WriteLine($"Error: {indexStatus.Reason}");
                        return 1;
                    }

                    ObjectResolutionResult objectResolution = service.Resolve(request);
                    switch (objectResolution.Status)
                    {
                        case ObjectResolutionStatus.Success:
                            resolved = objectResolution.Resolved;
                            break;
                        case ObjectResolutionStatus.IndexUnavailable:
                            Console.Error.WriteLine("Error: asset index not found. Run 'jiangyu assets index' first.");
                            return 1;
                        case ObjectResolutionStatus.NotFound:
                            Console.Error.WriteLine($"Error: no asset named '{name}' found{FormatClassSuffix(className)}.");
                            return 1;
                        case ObjectResolutionStatus.Ambiguous:
                            Console.Error.WriteLine($"Error: asset name '{name}' is ambiguous{FormatClassSuffix(className)}.");
                            foreach (var candidate in objectResolution.Candidates)
                            {
                                Console.Error.WriteLine($"  {candidate.Name} ({candidate.ClassName}) in {candidate.Collection} [pathId={candidate.PathId}]");
                            }
                            Console.Error.WriteLine("Rerun with --collection and --path-id.");
                            return 1;
                        default:
                            Console.Error.WriteLine($"Error: unsupported resolution status '{objectResolution.Status}'.");
                            return 1;
                    }
                }

                ObjectInspectionResult result = service.Inspect(request, resolved);
                string json = JsonSerializer.Serialize(result, output == "json" ? CompactJsonOptions : PrettyJsonOptions);
                Console.WriteLine(json);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: inspect failed: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    private static bool TryValidateInput(
        string? collection,
        long? pathId,
        string? name,
        string output,
        int maxDepth,
        int maxArraySampleLength,
        out string? error)
    {
        bool hasIdentity = !string.IsNullOrWhiteSpace(collection) || pathId.HasValue;
        bool hasPartialIdentity = !string.IsNullOrWhiteSpace(collection) ^ pathId.HasValue;
        bool hasName = !string.IsNullOrWhiteSpace(name);

        if (hasPartialIdentity)
        {
            error = "use both --collection and --path-id together.";
            return false;
        }

        if (hasIdentity == hasName)
        {
            error = "use either --collection + --path-id or --name.";
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

    private static string FormatClassSuffix(string? className)
    {
        return string.IsNullOrWhiteSpace(className) ? string.Empty : $" with type '{className}'";
    }
}
