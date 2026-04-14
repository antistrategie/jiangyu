using System.CommandLine;
using System.Text.Json;
using Jiangyu.Core.Glb;

namespace Jiangyu.Cli.Commands.Assets;

public static class InspectGlbCommand
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

    public static Command Create()
    {
        var glbOption = new Option<string>("--glb") { Description = "GLB/glTF file to inspect", Required = true };
        var filterOption = new Option<string?>("--filter") { Description = "Filter nodes by name" };
        var outOption = new Option<string?>("--out") { Description = "Output JSON path" };

        var command = new Command("glb", "Inspect raw GLB/glTF node hierarchy and skins")
        {
            glbOption,
            filterOption,
            outOption
        };
        command.SetAction((parseResult) =>
        {
            var glbPath = Path.GetFullPath(parseResult.GetRequiredValue(glbOption));
            var filter = parseResult.GetValue(filterOption);
            var outputPath = Path.GetFullPath(parseResult.GetValue(outOption) ?? Path.Combine(Path.GetTempPath(), "jiangyu-inspect-glb.json"));

            if (!File.Exists(glbPath))
            {
                Console.Error.WriteLine($"Error: GLB not found: {glbPath}");
                return 1;
            }

            try
            {
                var contract = GlbCharacterContract.Load(glbPath, filter);
                var report = BuildReport(glbPath, contract);

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, JsonSerializer.Serialize(report, PrettyJsonOptions));

                Console.WriteLine($"Wrote GLB inspection report to {outputPath}");
                Console.WriteLine($"  nodes: {report.Nodes.Count}");
                Console.WriteLine($"  skins: {report.Skins.Count}");
                if (!string.IsNullOrEmpty(report.CanonicalSkeletonRootPath))
                {
                    Console.WriteLine($"  canonical root: {report.CanonicalSkeletonRootPath}");
                    Console.WriteLine($"  suggested joint remaps: {report.SuggestedJointRemaps.Count}");
                }
                foreach (var node in report.Nodes.Take(20))
                    Console.WriteLine($"  {node.Path} pos={Format(node.LocalTranslation)} scale={Format(node.LocalScale)} mesh={node.MeshName ?? "-"} skin={node.SkinIndex?.ToString() ?? "-"}");

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: inspect-glb failed: {ex}");
                return 1;
            }
        });

        return command;
    }

    private static GlbInspectionReport BuildReport(string glbPath, GlbCharacterContract.CharacterContract contract) => new()
    {
        GlbPath = glbPath,
        Nodes = [.. contract.Nodes.Select(n => new GlbNodeInfo
        {
            Index = n.Index, Name = n.Name, Path = n.Path, MeshName = n.MeshName, SkinIndex = n.SkinIndex,
            LocalTranslation = n.LocalTranslation, LocalRotation = n.LocalRotation, LocalScale = n.LocalScale, Children = n.Children,
        })],
        Skins = [.. contract.Skins.Select(s => new GlbSkinInfo
        {
            Index = s.Index, Name = s.Name, JointCount = s.JointCount,
            Joints = [.. s.Joints.Select(j => new GlbSkinJointInfo { Index = j.Index, Name = j.Name, Path = j.Path, InverseBindMatrix = j.InverseBindMatrix })],
        })],
        CanonicalSkeletonRootPath = contract.CanonicalSkeletonRootPath,
        SuggestedJointRemaps = [.. contract.SuggestedJointRemaps.Select(r => new GlbJointRemapInfo
        {
            SkinIndex = r.SkinIndex, JointIndex = r.JointIndex, SourcePath = r.SourcePath, CanonicalPath = r.CanonicalPath,
        })],
    };

    private static string Format(float[] values)
        => $"({string.Join(", ", values.Select(v => v.ToString("0.####")))})";

    private sealed class GlbInspectionReport
    {
        public string GlbPath { get; set; } = "";
        public List<GlbNodeInfo> Nodes { get; set; } = [];
        public List<GlbSkinInfo> Skins { get; set; } = [];
        public string? CanonicalSkeletonRootPath { get; set; }
        public List<GlbJointRemapInfo> SuggestedJointRemaps { get; set; } = [];
    }

    private sealed class GlbNodeInfo
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
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
        public string Name { get; set; } = "";
        public int JointCount { get; set; }
        public List<GlbSkinJointInfo> Joints { get; set; } = [];
    }

    private sealed class GlbSkinJointInfo
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public float[][] InverseBindMatrix { get; set; } = [];
    }

    private sealed class GlbJointRemapInfo
    {
        public int SkinIndex { get; set; }
        public int JointIndex { get; set; }
        public string SourcePath { get; set; } = "";
        public string CanonicalPath { get; set; } = "";
    }
}
