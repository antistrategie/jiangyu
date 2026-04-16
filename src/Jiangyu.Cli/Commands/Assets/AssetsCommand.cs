using System.CommandLine;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Compile;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;

namespace Jiangyu.Cli.Commands.Assets;

public static class AssetsCommand
{
    public static Command Create()
    {
        var command = new Command("assets", "Asset pipeline: index, search, export, inspect")
        {
            CreateIndexCommand(),
            CreateSearchCommand(),
            CreateExportCommand(),
            CreateInspectCommand()
        };

        return command;
    }

    private static Command CreateIndexCommand()
    {
        var command = new Command("index", "Build searchable asset index from game data");
        command.SetAction((parseResult) =>
        {
            var resolution = EnvironmentContext.ResolveFromGlobalConfig();
            if (!resolution.Success)
            {
                Console.Error.WriteLine(resolution.Error);
                return 1;
            }

            var service = resolution.Context!.CreateAssetPipelineService(new ConsoleProgressSink(), new ConsoleLogSink());
            service.BuildIndex();
            return 0;
        });
        return command;
    }

    private static Command CreateSearchCommand()
    {
        var queryArg = new Argument<string>("query") { Description = "Search term (matches asset name)" };
        var typeOption = new Option<string?>("--type") { Description = "Filter by class name (e.g. Mesh, GameObject)" };

        var command = new Command("search", "Search the asset index")
        {
            queryArg,
            typeOption
        };
        command.SetAction((parseResult) =>
        {
            var query = parseResult.GetValue(queryArg) ?? "";
            var typeFilter = parseResult.GetValue(typeOption);

            var resolution = EnvironmentContext.ResolveFromGlobalConfig();
            if (!resolution.Success)
            {
                Console.Error.WriteLine(resolution.Error);
                return 1;
            }

            var service = resolution.Context!.CreateAssetPipelineService(new ConsoleProgressSink(), new ConsoleLogSink());
            CachedIndexStatus indexStatus = service.GetIndexStatus();
            if (!indexStatus.IsCurrent)
            {
                Console.Error.WriteLine($"Error: {indexStatus.Reason}");
                return 1;
            }

            var results = service.Search(query, typeFilter);
            if (results.Count == 0)
            {
                Console.WriteLine("No matching assets found.");
                return 0;
            }

            int nameWidth = Math.Min(results.Max(r => r.Name?.Length ?? 0), 50);
            int typeWidth = results.Max(r => r.ClassName?.Length ?? 0);

            foreach (var entry in results)
            {
                string name = entry.Name ?? "(unnamed)";
                if (name.Length > 50) name = name[..47] + "...";
                Console.WriteLine($"  {name.PadRight(nameWidth + 2)} {entry.ClassName?.PadRight(typeWidth + 2)} {entry.Collection}");
                if (!string.IsNullOrWhiteSpace(entry.CanonicalPath))
                {
                    Console.WriteLine($"    path: {entry.CanonicalPath}");
                }
                if (!string.IsNullOrWhiteSpace(entry.Name) &&
                    string.Equals(entry.ClassName, "PrefabHierarchyObject", StringComparison.Ordinal))
                {
                    Console.WriteLine($"    replacement target: {CompilationService.BuildModelReplacementRelativePath(entry.Name!, entry.PathId)}");
                }
            }

            if (results.Count == 50)
            {
                Console.WriteLine("  ... (showing first 50 results, refine your query)");
            }

            return 0;
        });

        return command;
    }

    private static Command CreateExportCommand()
    {
        var exportCommand = new Command("export", "Export game assets as authoring-ready model packages");

        var nameArg = new Argument<string>("name") { Description = "Asset name to export" };
        var outputOption = new Option<string?>("--output") { Description = "Output directory" };
        var rawOption = new Option<bool>("--raw") { Description = "Export raw GLB instead of clean glTF" };

        var modelCommand = new Command("model", "Export a game model")
        {
            nameArg,
            outputOption,
            rawOption
        };
        modelCommand.SetAction((parseResult) =>
        {
            var assetName = parseResult.GetRequiredValue(nameArg);
            var outputDir = parseResult.GetValue(outputOption);
            var raw = parseResult.GetValue(rawOption);

            var resolution = EnvironmentContext.ResolveFromGlobalConfig();
            if (!resolution.Success)
            {
                Console.Error.WriteLine(resolution.Error);
                return 1;
            }

            var service = resolution.Context!.CreateAssetPipelineService(new ConsoleProgressSink(), new ConsoleLogSink());
            CachedIndexStatus indexStatus = service.GetIndexStatus();
            if (!indexStatus.IsCurrent)
            {
                Console.Error.WriteLine($"Error: {indexStatus.Reason}");
                return 1;
            }

            var match = service.ResolveAsset(assetName, "GameObject", "Mesh");
            if (match is null)
            {
                Console.Error.WriteLine($"Error: no GameObject or Mesh named '{assetName}' in the index.");
                Console.Error.WriteLine("Run 'jiangyu assets search' to find available assets.");
                return 1;
            }

            var collection = match.Collection ?? "";
            var pathId = match.PathId;
            Console.WriteLine($"Found in index: {match.Name} ({match.ClassName}) in {collection} [pathId={pathId}]");

            var packageDir = outputDir ?? Path.Combine(service.CachePath, "exports", assetName);
            service.ExportModel(assetName, packageDir, clean: !raw, collection: collection, pathId: pathId);
            return 0;
        });

        exportCommand.Add(modelCommand);
        return exportCommand;
    }

    private static Command CreateInspectCommand()
    {
        var command = new Command("inspect", "Inspect and validate assets")
        {
            InspectPackageCommand.Create(),
            InspectGlbCommand.Create(),
            InspectObjectCommand.Create(),
            InspectCommand.Create(),
            InspectMeshCommand.Create()
        };

        return command;
    }
}
