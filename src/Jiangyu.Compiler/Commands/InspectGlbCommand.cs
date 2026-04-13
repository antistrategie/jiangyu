using System.Text.Json;
using Jiangyu.Compiler.Glb;
using SharpGLTF.Schema2;

namespace Jiangyu.Compiler.Commands;

public static class InspectGlbCommand
{
    public static Task<int> RunAsync(string[] args)
    {
        var options = ParseArgs(args);
        if (options is null)
        {
            PrintUsage();
            return Task.FromResult(1);
        }

        var resolved = options.Value;
        if (!File.Exists(resolved.GlbPath))
        {
            Console.Error.WriteLine($"Error: GLB not found: {resolved.GlbPath}");
            return Task.FromResult(1);
        }

        try
        {
            var report = Inspect(resolved);
            Directory.CreateDirectory(Path.GetDirectoryName(resolved.OutputPath)!);
            File.WriteAllText(resolved.OutputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

            Console.WriteLine($"Wrote GLB inspection report to {resolved.OutputPath}");
            Console.WriteLine($"  nodes: {report.Nodes.Count}");
            Console.WriteLine($"  skins: {report.Skins.Count}");
            if (!string.IsNullOrEmpty(report.CanonicalSkeletonRootPath))
            {
                Console.WriteLine($"  canonical root: {report.CanonicalSkeletonRootPath}");
                Console.WriteLine($"  suggested joint remaps: {report.SuggestedJointRemaps.Count}");
            }
            foreach (var node in report.Nodes.Take(20))
            {
                Console.WriteLine($"  {node.Path} pos={Format(node.LocalTranslation)} scale={Format(node.LocalScale)} mesh={node.MeshName ?? "-"} skin={node.SkinIndex?.ToString() ?? "-"}");
            }
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: inspect-glb failed: {ex}");
            return Task.FromResult(1);
        }
    }

    private static GlbInspectionReport Inspect(InspectGlbOptions options)
    {
        var contract = GlbCharacterContract.Load(options.GlbPath, options.Filter);
        return new GlbInspectionReport
        {
            GlbPath = options.GlbPath,
            Nodes = contract.Nodes.Select(node => new GlbNodeInfo
            {
                Index = node.Index,
                Name = node.Name,
                Path = node.Path,
                MeshName = node.MeshName,
                SkinIndex = node.SkinIndex,
                LocalTranslation = node.LocalTranslation,
                LocalRotation = node.LocalRotation,
                LocalScale = node.LocalScale,
                Children = node.Children,
            }).ToList(),
            Skins = contract.Skins.Select(skin => new GlbSkinInfo
            {
                Index = skin.Index,
                Name = skin.Name,
                JointCount = skin.JointCount,
                Joints = skin.Joints.Select(joint => new GlbSkinJointInfo
                {
                    Index = joint.Index,
                    Name = joint.Name,
                    Path = joint.Path,
                    InverseBindMatrix = joint.InverseBindMatrix,
                }).ToList(),
            }).ToList(),
            CanonicalSkeletonRootPath = contract.CanonicalSkeletonRootPath,
            SuggestedJointRemaps = contract.SuggestedJointRemaps.Select(remap => new GlbJointRemapInfo
            {
                SkinIndex = remap.SkinIndex,
                JointIndex = remap.JointIndex,
                SourcePath = remap.SourcePath,
                CanonicalPath = remap.CanonicalPath,
            }).ToList(),
        };
    }

    private static string Format(float[] values)
        => $"({string.Join(", ", values.Select(v => v.ToString("0.####")))})";

    private static InspectGlbOptions? ParseArgs(string[] args)
    {
        string? glbPath = null;
        var filter = string.Empty;
        var output = "/tmp/jiangyu-inspect-glb.json";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--glb":
                    glbPath = args[++i];
                    break;
                case "--filter":
                    filter = args[++i];
                    break;
                case "--out":
                    output = args[++i];
                    break;
                default:
                    return null;
            }
        }

        if (glbPath is null)
            return null;

        return new InspectGlbOptions(Path.GetFullPath(glbPath), filter, Path.GetFullPath(output));
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: jiangyu inspect-glb --glb <file.glb> [--filter <text>] [--out <json>]");
    }

    private readonly record struct InspectGlbOptions(string GlbPath, string Filter, string OutputPath);

    private sealed class GlbInspectionReport
    {
        public string GlbPath { get; set; } = string.Empty;
        public List<GlbNodeInfo> Nodes { get; set; } = [];
        public List<GlbSkinInfo> Skins { get; set; } = [];
        public string? CanonicalSkeletonRootPath { get; set; }
        public List<GlbJointRemapInfo> SuggestedJointRemaps { get; set; } = [];
    }

    private sealed class GlbNodeInfo
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string? MeshName { get; set; }
        public int? SkinIndex { get; set; }
        public float[] LocalTranslation { get; set; } = [0f, 0f, 0f];
        public float[] LocalRotation { get; set; } = [0f, 0f, 0f, 1f];
        public float[] LocalScale { get; set; } = [1f, 1f, 1f];
        public List<string> Children { get; set; } = [];
    }

    private sealed class GlbSkinInfo
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public int JointCount { get; set; }
        public List<GlbSkinJointInfo> Joints { get; set; } = [];
    }

    private sealed class GlbSkinJointInfo
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public float[][] InverseBindMatrix { get; set; } = [];
    }

    private sealed class GlbJointRemapInfo
    {
        public int SkinIndex { get; set; }
        public int JointIndex { get; set; }
        public string SourcePath { get; set; } = string.Empty;
        public string CanonicalPath { get; set; } = string.Empty;
    }
}
