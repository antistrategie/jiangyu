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
            int pathIdTokenWidth = results.Max(r => $"pathId={r.PathId}".Length);
            bool showClassColumn = string.IsNullOrWhiteSpace(typeFilter);
            int typeWidth = showClassColumn ? results.Max(r => r.ClassName?.Length ?? 0) : 0;

            foreach (var entry in results)
            {
                string name = entry.Name ?? "(unnamed)";
                if (name.Length > 50) name = name[..47] + "...";

                var classColumn = showClassColumn && !string.IsNullOrWhiteSpace(entry.ClassName)
                    ? $"  {entry.ClassName.PadRight(typeWidth)}"
                    : string.Empty;
                var collection = entry.Collection is null ? string.Empty : $"  \"{entry.Collection}\"";
                var pathIdToken = $"pathId={entry.PathId}".PadRight(pathIdTokenWidth);

                Console.WriteLine($"  {name.PadRight(nameWidth)}  {pathIdToken}{classColumn}{collection}");

                if (string.IsNullOrWhiteSpace(entry.Name))
                    continue;

                var replacementTarget = entry.ClassName switch
                {
                    "PrefabHierarchyObject" => CompilationService.BuildModelReplacementRelativePath(entry.Name!, entry.PathId),
                    "Texture2D" => CompilationService.BuildTextureReplacementRelativePath(entry.Name!, entry.PathId),
                    "Sprite" => CompilationService.BuildSpriteReplacementRelativePath(entry.Name!, entry.PathId),
                    "AudioClip" => CompilationService.BuildAudioReplacementRelativePath(entry.Name!, entry.PathId),
                    _ => null,
                };

                if (replacementTarget is not null)
                {
                    Console.WriteLine($"      → {replacementTarget}");
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
        var pathIdOption = new Option<long?>("--path-id") { Description = "Asset path ID (from 'assets search'). Required when the name matches more than one asset." };
        var collectionOption = new Option<string?>("--collection") { Description = "Asset collection (from 'assets search'). Optional narrowing filter." };

        var modelCommand = new Command("model", "Export a game model")
        {
            nameArg,
            outputOption,
            rawOption,
            pathIdOption,
            collectionOption
        };
        modelCommand.SetAction((parseResult) =>
        {
            var assetName = parseResult.GetRequiredValue(nameArg);
            var outputDir = parseResult.GetValue(outputOption);
            var raw = parseResult.GetValue(rawOption);
            var pathIdFilter = parseResult.GetValue(pathIdOption);
            var collectionFilter = parseResult.GetValue(collectionOption);

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

            var candidates = service.FindAssets(
                assetName,
                ["GameObject", "Mesh", "PrefabHierarchyObject"],
                collectionFilter,
                pathIdFilter);

            if (candidates.Count == 0)
            {
                Console.Error.WriteLine($"Error: no GameObject, Mesh, or PrefabHierarchyObject named '{assetName}' in the index" +
                    (pathIdFilter.HasValue ? $" with pathId={pathIdFilter.Value}" : "") +
                    (collectionFilter is not null ? $" in collection '{collectionFilter}'" : "") +
                    ".");
                Console.Error.WriteLine("Run 'jiangyu assets search' to find available assets.");
                return 1;
            }

            if (candidates.Count > 1)
            {
                Console.Error.WriteLine($"Error: '{assetName}' matches {candidates.Count} assets. Pass --path-id to pick one:");
                foreach (var candidate in candidates)
                {
                    Console.Error.WriteLine($"  {candidate.Name} ({candidate.ClassName}) in {candidate.Collection} [pathId={candidate.PathId}]");
                }
                return 1;
            }

            var match = candidates[0];
            AssetEntry exportTarget = match;

            if (!string.Equals(match.ClassName, "PrefabHierarchyObject", StringComparison.Ordinal))
            {
                try
                {
                    var index = service.LoadIndex()
                        ?? throw new InvalidOperationException("Asset index could not be loaded.");
                    exportTarget = AssetPipelineService.ResolveGameObjectBacking(index, match);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    return 1;
                }
            }

            var collection = exportTarget.Collection ?? "";
            var pathId = exportTarget.PathId;
            if (!ReferenceEquals(exportTarget, match))
            {
                Console.WriteLine($"Resolved PrefabHierarchyObject {match.Name} [pathId={match.PathId}] → backing GameObject [pathId={pathId}] in {collection}");
            }
            else
            {
                Console.WriteLine($"Found in index: {match.Name} ({match.ClassName}) in {collection} [pathId={pathId}]");
            }

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
