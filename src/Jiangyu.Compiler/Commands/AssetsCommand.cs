using Jiangyu.Compiler.Models;
using Jiangyu.Compiler.Services;

namespace Jiangyu.Compiler.Commands;

public static class AssetsCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        return args[0] switch
        {
            "index" => await IndexAsync(args[1..]),
            "search" => await SearchAsync(args[1..]),
            "export" => await ExportAsync(args[1..]),
            "inspect" => await InspectAsync(args[1..]),
            _ => PrintUsage(),
        };
    }

    private static async Task<int> IndexAsync(string[] args)
    {
        var (service, error) = CreateService();
        if (service is null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        service.BuildIndex();
        return 0;
    }

    private static async Task<int> SearchAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: jiangyu assets search <query> [--type <className>]");
            return 1;
        }

        var (service, error) = CreateService();
        if (service is null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var index = service.LoadIndex();
        if (index?.Assets is null)
        {
            Console.Error.WriteLine("No index found. Run 'jiangyu assets index' first.");
            return 1;
        }

        // Parse args: query and optional --type filter
        string? typeFilter = null;
        string query = "";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--type" && i + 1 < args.Length)
            {
                typeFilter = args[++i];
            }
            else if (!args[i].StartsWith("--"))
            {
                query = args[i];
            }
        }

        var results = index.Assets
            .Where(a =>
                (string.IsNullOrEmpty(query) || (a.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                && (typeFilter is null || string.Equals(a.ClassName, typeFilter, StringComparison.OrdinalIgnoreCase)))
            .Take(50)
            .ToList();

        if (results.Count == 0)
        {
            Console.WriteLine("No matching assets found.");
            return 0;
        }

        // Find column widths
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
            Console.WriteLine($"  ... (showing first 50 results, refine your query)");
        }

        return 0;
    }

    private static async Task<int> ExportAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "model" || args.Length < 2)
        {
            Console.Error.WriteLine("Usage: jiangyu assets export model <name> [--output <dir>]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Exports a game asset as a self-contained model package:");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Clean (default):  <dir>/model.gltf + textures/*.png");
            Console.Error.WriteLine("  Raw (--raw):      <dir>/model.glb");
            return 1;
        }

        string assetName = args[1];
        bool raw = args.Contains("--raw");

        var (service, error) = CreateService();
        if (service is null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        // Resolve asset identity from index (collection + pathId)
        var index = service.LoadIndex();
        if (index?.Assets is null)
        {
            Console.Error.WriteLine("Error: no index found. Run 'jiangyu assets index' first.");
            return 1;
        }

        var match = index.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase)
            && a.ClassName is "GameObject" or "Mesh");
        if (match is null)
        {
            Console.Error.WriteLine($"Error: no GameObject or Mesh named '{assetName}' in the index.");
            Console.Error.WriteLine("Run 'jiangyu assets search' to find available assets.");
            return 1;
        }

        var collection = match.Collection;
        var pathId = match.PathId;
        Console.WriteLine($"Found in index: {match.Name} ({match.ClassName}) in {match.Collection} [pathId={match.PathId}]");

        // Parse optional --output, default to cache/exports/<name>/
        string? packageDir = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--output" && i + 1 < args.Length)
            {
                packageDir = args[++i];
            }
        }

        packageDir ??= Path.Combine(service.CachePath, "exports", assetName);

        service.ExportModel(assetName, packageDir, clean: !raw, collection: collection, pathId: pathId);
        return 0;
    }

    private static (ExtractionService? service, string? error) CreateService()
    {
        var (gameDataPath, error) = GlobalConfig.ResolveGameDataPath();
        if (gameDataPath is null)
        {
            return (null, error);
        }

        var cachePath = GlobalConfig.Load().GetCachePath();
        return (new ExtractionService(gameDataPath, cachePath), null);
    }

    private static async Task<int> InspectAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintInspectUsage();
            return 1;
        }

        return args[0] switch
        {
            "prefab" => await InspectCommand.RunAsync(args[1..]),
            "glb" => await InspectGlbCommand.RunAsync(args[1..]),
            "mesh" => await InspectMeshCommand.RunAsync(args[1..]),
            "package" => await InspectPackageCommand.RunAsync(args[1..]),
            _ => PrintInspectUsage(),
        };
    }

    private static int PrintInspectUsage()
    {
        Console.Error.WriteLine("Usage: jiangyu assets inspect <type>");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Types:");
        Console.Error.WriteLine("  package    Validate an exported model package (.gltf + textures)");
        Console.Error.WriteLine("  glb        Inspect raw GLB/glTF node hierarchy and skins");
        Console.Error.WriteLine("  prefab     Compare game vs bundle prefab hierarchies (advanced)");
        Console.Error.WriteLine("  mesh       Inspect serialised mesh contract fields (advanced)");
        return 1;
    }

    private static int PrintUsage()
    {
        Console.Error.WriteLine("Usage: jiangyu assets <command>");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Commands:");
        Console.Error.WriteLine("  index      Build searchable asset index from game data");
        Console.Error.WriteLine("  search     Search the asset index");
        Console.Error.WriteLine("  export     Export game assets as authoring-ready model packages");
        Console.Error.WriteLine("  inspect    Inspect and validate assets");
        return 1;
    }
}
