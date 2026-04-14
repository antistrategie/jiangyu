using System.CommandLine;
using System.Text.Json;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;

namespace Jiangyu.Cli.Commands;

public static class InspectMeshCommand
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

    public static Command Create()
    {
        var bundleOption = new Option<string>("--bundle") { Description = "AssetBundle file to inspect", Required = true };
        var meshOption = new Option<string>("--mesh") { Description = "Mesh name to inspect", Required = true };
        var outOption = new Option<string?>("--out") { Description = "Output JSON path" };

        var command = new Command("mesh", "Inspect serialised mesh contract fields (advanced)");
        command.Add(bundleOption);
        command.Add(meshOption);
        command.Add(outOption);

        command.SetAction((ctx) =>
        {
            var bundlePath = Path.GetFullPath(ctx.GetRequiredValue(bundleOption));
            var meshName = ctx.GetRequiredValue(meshOption);
            var outputPath = Path.GetFullPath(ctx.GetValue(outOption) ?? Path.Combine(Path.GetTempPath(), "jiangyu-inspect-mesh.json"));

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
                var service = new MeshInspectionService();
                var report = service.Inspect(bundlePath, gameDataPath, meshName);

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, JsonSerializer.Serialize(report, PrettyJsonOptions));

                Console.WriteLine($"Wrote mesh inspection report to {outputPath}");
                Console.WriteLine($"  bundle matches: {report.BundleMeshes.Count}");
                Console.WriteLine($"  game matches:   {report.GameMeshes.Count}");

                foreach (var mesh in report.GameMeshes.Take(2))
                    PrintMesh("game", mesh);
                foreach (var mesh in report.BundleMeshes.Take(2))
                    PrintMesh("bundle", mesh);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: inspect-mesh failed: {ex}");
            }
        });

        return command;
    }

    private static void PrintMesh(string label, MeshInfo mesh)
    {
        Console.WriteLine($"[{label}] {Path.GetFileName(mesh.FilePath)} mesh={mesh.Name} pathId={mesh.PathId}");
        Console.WriteLine($"  bindPoses={mesh.BindPoseCount} boneHashes={mesh.BoneNameHashes.Count} rootBoneHash={mesh.RootBoneNameHash?.ToString() ?? "null"}");
        Console.WriteLine($"  bonesAabb={mesh.BonesAabbCount} variableBoneWeights={mesh.VariableBoneCountWeightsBytes}");
        Console.WriteLine($"  vertexCount={mesh.VertexCount} channels={mesh.ChannelCount} dataSize={mesh.VertexDataSize}");
        for (var i = 0; i < Math.Min(6, mesh.Channels.Count); i++)
            Console.WriteLine($"  channel[{i}]={mesh.Channels[i]}");
        for (var i = 0; i < mesh.VertexSamples.Count; i++)
            Console.WriteLine($"  sample[{i}]={mesh.VertexSamples[i]}");
        for (var i = 0; i < Math.Min(2, mesh.BindPoseSamples.Count); i++)
            Console.WriteLine($"  bind[{i}]={mesh.BindPoseSamples[i]}");
    }
}
