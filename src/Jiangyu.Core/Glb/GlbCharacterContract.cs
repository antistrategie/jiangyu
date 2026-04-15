using SharpGLTF.Schema2;

namespace Jiangyu.Core.Glb;

public static class GlbCharacterContract
{
    public sealed class CharacterContract
    {
        public required string GlbPath { get; init; }
        public required List<NodeInfo> Nodes { get; init; }
        public required List<SkinInfo> Skins { get; init; }
        public string? CanonicalSkeletonRootPath { get; init; }
        public required List<JointRemapInfo> SuggestedJointRemaps { get; init; }
    }

    public sealed class NodeInfo
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        public required string Path { get; init; }
        public string? MeshName { get; init; }
        public int? SkinIndex { get; init; }
        public required float[] LocalTranslation { get; init; }
        public required float[] LocalRotation { get; init; }
        public required float[] LocalScale { get; init; }
        public required List<string> Children { get; init; }
    }

    public sealed class SkinInfo
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        public required int JointCount { get; init; }
        public required List<SkinJointInfo> Joints { get; init; }
    }

    public sealed class SkinJointInfo
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        public required string Path { get; init; }
        public required float[][] InverseBindMatrix { get; init; }
    }

    public sealed class JointRemapInfo
    {
        public required int SkinIndex { get; init; }
        public required int JointIndex { get; init; }
        public required string SourcePath { get; init; }
        public required string CanonicalPath { get; init; }
    }

    public static CharacterContract Load(string glbPath, string? filter = null)
    {
        var model = ModelRoot.Load(glbPath);
        var skins = new List<SkinInfo>();
        foreach (var skin in model.LogicalSkins)
        {
            var joints = new List<SkinJointInfo>();
            for (var i = 0; i < skin.JointsCount; i++)
            {
                var (joint, inverseBindMatrix) = skin.GetJoint(i);
                joints.Add(new SkinJointInfo
                {
                    Index = i,
                    Name = joint.Name ?? $"Joint_{i}",
                    Path = BuildNodePath(joint),
                    InverseBindMatrix = ToMatrixRows(inverseBindMatrix),
                });
            }

            skins.Add(new SkinInfo
            {
                Index = skin.LogicalIndex,
                Name = skin.Name ?? $"Skin_{skin.LogicalIndex}",
                JointCount = skin.JointsCount,
                Joints = joints,
            });
        }

        var nodes = new List<NodeInfo>();
        foreach (var node in model.LogicalNodes)
        {
            var path = BuildNodePath(node);
            if (!string.IsNullOrEmpty(filter) &&
                path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                (node.Name?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) < 0)
            {
                continue;
            }

            var local = node.LocalTransform;
            nodes.Add(new NodeInfo
            {
                Index = node.LogicalIndex,
                Name = node.Name ?? $"Node_{node.LogicalIndex}",
                Path = path,
                MeshName = node.Mesh?.Name,
                SkinIndex = node.Skin?.LogicalIndex,
                LocalTranslation = [local.Translation.X, local.Translation.Y, local.Translation.Z],
                LocalRotation = [local.Rotation.X, local.Rotation.Y, local.Rotation.Z, local.Rotation.W],
                LocalScale = [local.Scale.X, local.Scale.Y, local.Scale.Z],
                Children = [.. node.VisualChildren.Select(c => c.Name ?? $"Node_{c.LogicalIndex}")],
            });
        }

        nodes = [.. nodes.OrderBy(n => n.Path, StringComparer.OrdinalIgnoreCase)];
        var canonicalRoot = FindCanonicalRootPath(model);
        var remaps = new List<JointRemapInfo>();
        if (!string.IsNullOrEmpty(canonicalRoot))
        {
            var canonicalPathsBySuffix = BuildCanonicalPathsBySuffix(nodes, canonicalRoot);
            foreach (var skin in skins)
            {
                foreach (var joint in skin.Joints)
                {
                    var suffix = GetPathSuffixFromHips(joint.Path);
                    if (suffix is null)
                        continue;

                    if (canonicalPathsBySuffix.TryGetValue(suffix, out var canonicalPath))
                    {
                        remaps.Add(new JointRemapInfo
                        {
                            SkinIndex = skin.Index,
                            JointIndex = joint.Index,
                            SourcePath = joint.Path,
                            CanonicalPath = canonicalPath,
                        });
                    }
                }
            }
        }

        return new CharacterContract
        {
            GlbPath = glbPath,
            Nodes = nodes,
            Skins = skins,
            CanonicalSkeletonRootPath = canonicalRoot,
            SuggestedJointRemaps = remaps,
        };
    }

    internal static string BuildNodePath(Node node)
    {
        var parts = new List<string>();
        Node? current = node;
        while (current != null)
        {
            parts.Add(current.Name ?? $"Node_{current.LogicalIndex}");
            current = current.VisualParent;
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    private static string? FindCanonicalRootPath(ModelRoot model)
    {
        foreach (var node in model.LogicalNodes)
        {
            var path = BuildNodePath(node);
            // JIANGYU-CONTRACT: current MENACE character authoring path assumes a canonical
            // skeleton root named "Root" with a direct "Hips" child. This is acceptable for
            // the proven character replacement workflow, but it is not a general skeleton rule
            // for all future prefab/model work and should be re-validated when broadening scope.
            if (!path.EndsWith("/Root", StringComparison.Ordinal))
                continue;

            if (node.VisualChildren.Any(c => string.Equals(c.Name, "Hips", StringComparison.Ordinal)))
                return path;
        }

        return null;
    }

    private static Dictionary<string, string> BuildCanonicalPathsBySuffix(IEnumerable<NodeInfo> nodes, string canonicalRootPath)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!node.Path.StartsWith(canonicalRootPath + "/", StringComparison.Ordinal))
                continue;

            var suffix = GetPathSuffixFromHips(node.Path);
            if (suffix is null)
                continue;

            map[suffix] = node.Path;
        }

        return map;
    }

    private static string? GetPathSuffixFromHips(string path)
    {
        var marker = "/Hips";
        // JIANGYU-CONTRACT: joint remap suffixes are currently normalized from the first
        // "Hips" segment because that matches the proven MENACE character rigs Jiangyu was
        // validated against. Keep this scoped to character-rig handling until generalized.
        var index = path.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return null;

        return path[(index + 1)..];
    }

    private static float[][] ToMatrixRows(System.Numerics.Matrix4x4 matrix)
    {
        return
        [
            [matrix.M11, matrix.M12, matrix.M13, matrix.M14],
            [matrix.M21, matrix.M22, matrix.M23, matrix.M24],
            [matrix.M31, matrix.M32, matrix.M33, matrix.M34],
            [matrix.M41, matrix.M42, matrix.M43, matrix.M44],
        ];
    }
}
