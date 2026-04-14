using System.CommandLine;
using System.Text.Json;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;

namespace Jiangyu.Cli.Commands;

public static class InspectCommand
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

    public static Command Create()
    {
        var bundleOption = new Option<string>("--bundle") { Description = "AssetBundle file to inspect", Required = true };
        var filterOption = new Option<string>("--filter") { Description = "Game asset name filter", DefaultValueFactory = _ => "basic_soldier" };
        var bundleFilterOption = new Option<string?>("--bundle-filter") { Description = "Bundle asset name filter (defaults to --filter value)" };
        var outOption = new Option<string?>("--out") { Description = "Output JSON path" };

        var command = new Command("prefab", "Compare game vs bundle prefab hierarchies (advanced)")
        {
            bundleOption,
            filterOption,
            bundleFilterOption,
            outOption
        };

        command.SetAction((ctx) =>
        {
            var bundlePath = Path.GetFullPath(ctx.GetRequiredValue(bundleOption));
            var gameFilter = ctx.GetValue(filterOption) ?? "basic_soldier";
            var bundleFilter = ctx.GetValue(bundleFilterOption) ?? gameFilter;
            var outputPath = Path.GetFullPath(ctx.GetValue(outOption) ?? Path.Combine(Path.GetTempPath(), "jiangyu-inspect.json"));

            if (!File.Exists(bundlePath))
            {
                Console.Error.WriteLine($"Error: bundle not found: {bundlePath}");
                return;
            }

            var (gameDataPath, error) = GlobalConfig.ResolveGameDataPath();
            if (gameDataPath is null)
            {
                Console.Error.WriteLine(error);
                return;
            }

            if (!Directory.Exists(gameDataPath))
            {
                Console.Error.WriteLine($"Error: game data directory not found: {gameDataPath}");
                return;
            }

            try
            {
                var service = new AssetInspectionService();
                var report = AssetInspectionService.InspectBundles(bundlePath, gameDataPath, gameFilter, bundleFilter);

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, JsonSerializer.Serialize(report, PrettyJsonOptions));

                var bundleMatches = report.BundleFiles.Sum(f => f.GameObjects.Count);
                var gameMatches = report.GameFiles.Sum(f => f.GameObjects.Count);
                Console.WriteLine($"Wrote inspection report to {outputPath}");
                Console.WriteLine($"  bundle matches: {bundleMatches}");
                Console.WriteLine($"  game matches:   {gameMatches}");

                foreach (var file in report.GameFiles.Where(f => f.GameObjects.Count > 0))
                {
                    Console.WriteLine($"[game] {Path.GetFileName(file.Path)}");
                    foreach (var go in file.GameObjects.Take(8))
                        PrintGameObjectSummary(go);
                }

                foreach (var file in report.BundleFiles.Where(f => f.GameObjects.Count > 0))
                {
                    Console.WriteLine($"[bundle] {Path.GetFileName(file.Path)}");
                    foreach (var go in file.GameObjects.Take(8))
                        PrintGameObjectSummary(go);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: inspect failed: {ex}");
            }
        });

        return command;
    }

    private static void PrintGameObjectSummary(GameObjectInfo go)
    {
        var compSummary = string.Join(", ", go.Components.Select(c => c.TypeName));
        Console.WriteLine($"  {go.Path} [{compSummary}]");
        if (go.Transform is not null)
            Console.WriteLine($"    Transform pos={FormatVector(go.Transform.LocalPosition)} scale={FormatVector(go.Transform.LocalScale)} children={go.Transform.ChildNames.Count}");
        foreach (var smr in go.SkinnedMeshRenderers)
            Console.WriteLine($"    SMR mesh={smr.MeshName ?? smr.MeshPathId?.ToString() ?? "null"} root={smr.RootBonePath ?? smr.RootBoneName ?? smr.RootBonePathId?.ToString() ?? "null"} bones={smr.BoneNames.Count} mats={smr.MaterialCount}");
        foreach (var animator in go.Animators)
            Console.WriteLine($"    Animator avatar={animator.AvatarPathId?.ToString() ?? "null"} controller={animator.ControllerPathId?.ToString() ?? "null"} culling={animator.CullingMode}");
        foreach (var lod in go.LodGroups)
            Console.WriteLine($"    LODGroup lods={lod.Lods.Count}");
    }

    private static string FormatVector(float[] values)
        => $"({string.Join(", ", values.Select(v => v.ToString("0.####")))})";
}
