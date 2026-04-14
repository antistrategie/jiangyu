using System.CommandLine;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;

namespace Jiangyu.Cli.Commands;

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
        command.SetAction((ctx) =>
        {
            var (service, error) = CreateService();
            if (service is null)
            {
                Console.Error.WriteLine(error);
                return;
            }

            service.BuildIndex();
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
        command.SetAction((ctx) =>
        {
            var query = ctx.GetValue(queryArg) ?? "";
            var typeFilter = ctx.GetValue(typeOption);

            var (service, error) = CreateService();
            if (service is null)
            {
                Console.Error.WriteLine(error);
                return;
            }

            var results = service.Search(query, typeFilter);
            if (results.Count == 0)
            {
                Console.WriteLine("No matching assets found.");
                return;
            }

            int nameWidth = Math.Min(results.Max(r => r.Name?.Length ?? 0), 50);
            int typeWidth = results.Max(r => r.ClassName?.Length ?? 0);

            foreach (var entry in results)
            {
                string name = entry.Name ?? "(unnamed)";
                if (name.Length > 50) name = name[..47] + "...";
                Console.WriteLine($"  {name.PadRight(nameWidth + 2)} {entry.ClassName?.PadRight(typeWidth + 2)} {entry.Collection}");
            }

            if (results.Count == 50)
            {
                Console.WriteLine("  ... (showing first 50 results, refine your query)");
            }
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
        modelCommand.SetAction((ctx) =>
        {
            var assetName = ctx.GetRequiredValue(nameArg);
            var outputDir = ctx.GetValue(outputOption);
            var raw = ctx.GetValue(rawOption);

            var (service, error) = CreateService();
            if (service is null)
            {
                Console.Error.WriteLine(error);
                return;
            }

            var match = service.ResolveAsset(assetName, "GameObject", "Mesh");
            if (match is null)
            {
                Console.Error.WriteLine($"Error: no GameObject or Mesh named '{assetName}' in the index.");
                Console.Error.WriteLine("Run 'jiangyu assets search' to find available assets.");
                return;
            }

            var collection = match.Collection ?? "";
            var pathId = match.PathId;
            Console.WriteLine($"Found in index: {match.Name} ({match.ClassName}) in {collection} [pathId={pathId}]");

            var packageDir = outputDir ?? Path.Combine(service.CachePath, "exports", assetName);
            service.ExportModel(assetName, packageDir, clean: !raw, collection: collection, pathId: pathId);
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
            InspectCommand.Create(),
            InspectMeshCommand.Create()
        };

        return command;
    }

    private static (AssetPipelineService? service, string? error) CreateService()
    {
        var (gameDataPath, error) = GlobalConfig.ResolveGameDataPath();
        if (gameDataPath is null)
        {
            return (null, error);
        }

        var cachePath = GlobalConfig.Load().GetCachePath();
        var progress = new ConsoleProgressSink();
        var log = new ConsoleLogSink();
        return (new AssetPipelineService(gameDataPath, cachePath, progress, log), null);
    }
}
