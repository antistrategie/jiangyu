using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace Jiangyu.Compiler.Services;

/// <summary>
/// Rebuilds a raw extracted GLB into a clean authoring GLB.
/// Only keeps: prefab root, canonical skeleton, and mesh carriers.
/// Drops: LOD containers, particle nodes, other prefab debris.
/// Bakes container scale into vertices so modders get a 1x metre-scale model.
///
/// Container scale detection is a validated structural rule for current tested
/// asset categories (infantry, aliens, constructs, vehicles, weapons) — not a
/// claimed universal truth for all future asset shapes. If a new asset category
/// breaks the assumption, check FindSkinnedMeshContainerScale and
/// FindAncestorContainerScale.
/// </summary>
internal static class ModelCleanupService
{
    private const float ContainerScaleThreshold = 0.02f;

    public static void CleanupGlb(string glbPath)
    {
        var source = ModelRoot.Load(glbPath);

        // Find the skeleton root: the first direct child of the prefab root
        // whose subtree contains skin joints.
        var skeletonSourceRoot = FindSkeletonRoot(source);

        // Build the clean scene
        var scene = new SceneBuilder();
        var sourceRoot = source.LogicalNodes[0];
        var rootNode = new NodeBuilder(sourceRoot.Name);
        scene.AddNode(rootNode);

        // Map source node index → clean NodeBuilder
        var nodeMap = new Dictionary<int, NodeBuilder>
        {
            [sourceRoot.LogicalIndex] = rootNode
        };

        bool hasSkins = skeletonSourceRoot is not null;

        if (hasSkins)
        {
            // Skinned model: rebuild only the skeleton subtree, drop prefab debris
            var skeletonNode = rootNode.CreateNode(skeletonSourceRoot!.Name);
            CopyNodeTransform(skeletonSourceRoot, skeletonNode);
            nodeMap[skeletonSourceRoot.LogicalIndex] = skeletonNode;
            RebuildSkeletonSubtree(skeletonSourceRoot, skeletonNode, nodeMap);
        }
        else
        {
            // Static-mesh-only model: rebuild the full hierarchy
            // (wheel mounts, turret pivots, etc. are structurally meaningful)
            RebuildFullHierarchy(sourceRoot, rootNode, nodeMap);
        }

        // Rebuild meshes
        foreach (var sourceNode in source.LogicalNodes)
        {
            if (sourceNode.Mesh is null)
            {
                continue;
            }

            var meshName = sourceNode.Mesh.Name ?? $"Mesh_{sourceNode.Mesh.LogicalIndex}";

            // Determine the scale to bake into vertices.
            // For skinned meshes: SharpGLTF places them at the scene root, not under
            // the container nodes, so parent chain walk won't find the 0.01 scale.
            // Instead, find the container node that originally held this mesh's SMR
            // by looking for any small-scale node whose name relates to the mesh.
            // For static meshes: trace the actual parent chain.
            float meshScale = sourceNode.Skin is not null
                ? FindSkinnedMeshContainerScale(source, sourceNode)
                : FindAncestorContainerScale(sourceNode);
            var meshBuilder = RebuildMesh(sourceNode.Mesh, meshScale, meshName);

            if (sourceNode.Skin is not null)
            {
                // Skinned mesh — bind to the clean skeleton joints
                var skin = sourceNode.Skin;
                var jointNodes = new NodeBuilder[skin.JointsCount];
                for (int i = 0; i < skin.JointsCount; i++)
                {
                    var (joint, _) = skin.GetJoint(i);
                    if (nodeMap.TryGetValue(joint.LogicalIndex, out var cleanJoint))
                    {
                        jointNodes[i] = cleanJoint;
                    }
                    else
                    {
                        jointNodes[i] = rootNode.CreateNode($"UnmappedJoint_{i}");
                    }
                }

                scene.AddSkinnedMesh(meshBuilder, Matrix4x4.Identity, jointNodes);
            }
            else
            {
                // Static mesh — attach to its parent in the hierarchy
                var parentNode = nodeMap.TryGetValue(sourceNode.LogicalIndex, out var existing)
                    ? existing
                    : (sourceNode.VisualParent is not null && nodeMap.TryGetValue(sourceNode.VisualParent.LogicalIndex, out var parent)
                        ? parent
                        : rootNode);
                scene.AddRigidMesh(meshBuilder, parentNode);
            }
        }

        // Write the clean GLB, replacing the raw one
        var model = scene.ToGltf2();
        model.SaveGLB(glbPath);
    }

    /// <summary>
    /// For skinned meshes, finds the container scale from the original hierarchy.
    /// SharpGLTF places skinned mesh nodes at the scene root, so parent chain walk
    /// won't find the container. Instead, we find the original node that had this
    /// skin's SkinnedMeshRenderer by looking for nodes in the hierarchy that reference
    /// the same skin index, then trace THAT node's parent chain for the container scale.
    /// </summary>
    private static float FindSkinnedMeshContainerScale(ModelRoot model, Node meshNode)
    {
        if (meshNode.Skin is null || meshNode.Skin.JointsCount == 0)
        {
            Console.Error.WriteLine($"  [cleanup] WARNING: skinned mesh '{meshNode.Mesh?.Name}' has no joints, scale=1.0");
            return 1f;
        }

        var (firstJoint, _) = meshNode.Skin.GetJoint(0);

        // Walk up from the first joint to find the skeleton root
        // (highest ancestor before the prefab root)
        var skeletonRoot = firstJoint;
        while (skeletonRoot.VisualParent is not null &&
               skeletonRoot.VisualParent.VisualParent is not null)
        {
            skeletonRoot = skeletonRoot.VisualParent;
        }

        var prefabRoot = skeletonRoot.VisualParent;
        if (prefabRoot is null)
        {
            Console.Error.WriteLine($"  [cleanup] WARNING: skeleton root '{skeletonRoot.Name}' has no parent, scale=1.0");
            return 1f;
        }

        // Find small-scale container siblings of the skeleton root
        var candidates = prefabRoot.VisualChildren
            .Where(n => IsUniformSmallScale(n.LocalTransform.Scale))
            .ToList();

        if (candidates.Count == 0)
        {
            Console.WriteLine($"  [cleanup] mesh='{meshNode.Mesh?.Name}' joint0='{firstJoint.Name}' skelRoot='{skeletonRoot.Name}' -> no container siblings, scale=1.0");
            return 1f;
        }

        if (candidates.Count > 1)
        {
            var scales = candidates.Select(c => c.LocalTransform.Scale.X).Distinct().ToList();
            if (scales.Count > 1)
            {
                Console.Error.WriteLine($"  [cleanup] WARNING: mesh='{meshNode.Mesh?.Name}' has {candidates.Count} container siblings with different scales: {string.Join(", ", scales)}. Using first.");
            }
        }

        float scale = candidates[0].LocalTransform.Scale.X;
        Console.WriteLine($"  [cleanup] mesh='{meshNode.Mesh?.Name}' joint0='{firstJoint.Name}' skelRoot='{skeletonRoot.Name}' container='{candidates[0].Name}' -> scale={scale}");
        return scale;
    }

    /// <summary>
    /// Traces a mesh node's parent chain to find the cumulative container scale.
    /// If the mesh sits under a 0.01-scaled LOD container, returns 0.01.
    /// If no small-scale ancestor exists, returns 1.0.
    /// </summary>
    private static float FindAncestorContainerScale(Node node)
    {
        float scale = 1f;
        var current = node.VisualParent;
        while (current is not null)
        {
            var s = current.LocalTransform.Scale;
            if (IsUniformSmallScale(s))
            {
                scale *= s.X;
            }
            current = current.VisualParent;
        }
        return scale;
    }

    private static void RebuildFullHierarchy(Node sourceNode, NodeBuilder cleanNode, Dictionary<int, NodeBuilder> nodeMap)
    {
        foreach (var child in sourceNode.VisualChildren)
        {
            // Skip LOD container nodes
            if (IsUniformSmallScale(child.LocalTransform.Scale))
            {
                continue;
            }

            var childClean = cleanNode.CreateNode(child.Name);
            CopyNodeTransform(child, childClean);
            nodeMap[child.LogicalIndex] = childClean;
            RebuildFullHierarchy(child, childClean, nodeMap);
        }
    }

    private static void RebuildSkeletonSubtree(Node sourceNode, NodeBuilder cleanNode, Dictionary<int, NodeBuilder> jointMap)
    {
        foreach (var child in sourceNode.VisualChildren)
        {
            var childClean = cleanNode.CreateNode(child.Name);
            CopyNodeTransform(child, childClean);
            jointMap[child.LogicalIndex] = childClean;
            RebuildSkeletonSubtree(child, childClean, jointMap);
        }
    }

    private static void CopyNodeTransform(Node source, NodeBuilder target)
    {
        target.LocalTransform = new SharpGLTF.Transforms.AffineTransform(
            SnapScale(source.LocalTransform.Scale),
            source.LocalTransform.Rotation,
            source.LocalTransform.Translation);
    }

    private static IMeshBuilder<MaterialBuilder> RebuildMesh(Mesh sourceMesh, float vertexScale, string meshName)
    {
        bool hasSkinning = sourceMesh.Primitives.Any(p =>
            p.GetVertexAccessor("JOINTS_0") is not null &&
            p.GetVertexAccessor("WEIGHTS_0") is not null);

        if (hasSkinning)
        {
            return RebuildSkinnedMesh(sourceMesh, vertexScale, meshName);
        }

        return RebuildStaticMesh(sourceMesh, vertexScale, meshName);
    }

    private static IMeshBuilder<MaterialBuilder> RebuildSkinnedMesh(Mesh sourceMesh, float vertexScale, string meshName)
    {
        var meshBuilder = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>(meshName);

        foreach (var primitive in sourceMesh.Primitives)
        {
            var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array();
            if (positions is null) continue;

            var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
            var uvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
            var joints = primitive.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
            var weights = primitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();

            var material = new MaterialBuilder(primitive.Material?.Name ?? "default");
            var primBuilder = meshBuilder.UsePrimitive(material);
            var indices = primitive.GetIndices().ToArray();
            int vertexCount = positions.Count;

            var vertices = new (VertexPositionNormal, VertexTexture1, VertexJoints4)[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var pos = positions[i] * vertexScale;
                var normal = normals is not null && i < normals.Count ? normals[i] : Vector3.UnitY;
                var uv = uvs is not null && i < uvs.Count ? uvs[i] : Vector2.Zero;

                var j = joints is not null && i < joints.Count ? joints[i] : Vector4.Zero;
                var w = weights is not null && i < weights.Count ? weights[i] : Vector4.Zero;

                vertices[i] = (
                    new VertexPositionNormal(pos, normal),
                    new VertexTexture1(uv),
                    new VertexJoints4(((int)j.X, w.X), ((int)j.Y, w.Y), ((int)j.Z, w.Z), ((int)j.W, w.W)));
            }

            for (int i = 0; i < indices.Length; i += 3)
            {
                primBuilder.AddTriangle(vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]]);
            }
        }

        return meshBuilder;
    }

    private static IMeshBuilder<MaterialBuilder> RebuildStaticMesh(Mesh sourceMesh, float vertexScale, string meshName)
    {
        var meshBuilder = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(meshName);

        foreach (var primitive in sourceMesh.Primitives)
        {
            var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array();
            if (positions is null) continue;

            var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
            var uvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();

            var material = new MaterialBuilder(primitive.Material?.Name ?? "default");
            var primBuilder = meshBuilder.UsePrimitive(material);
            var indices = primitive.GetIndices().ToArray();
            int vertexCount = positions.Count;

            var vertices = new (VertexPositionNormal, VertexTexture1, VertexEmpty)[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var pos = positions[i] * vertexScale;
                var normal = normals is not null && i < normals.Count ? normals[i] : Vector3.UnitY;
                var uv = uvs is not null && i < uvs.Count ? uvs[i] : Vector2.Zero;

                vertices[i] = (new VertexPositionNormal(pos, normal), new VertexTexture1(uv), default);
            }

            for (int i = 0; i < indices.Length; i += 3)
            {
                primBuilder.AddTriangle(vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]]);
            }
        }

        return meshBuilder;
    }

    private static Node? FindSkeletonRoot(ModelRoot model)
    {
        if (model.LogicalSkins.Count == 0)
        {
            return null;
        }

        var jointNodes = new HashSet<int>();
        foreach (var skin in model.LogicalSkins)
        {
            for (int i = 0; i < skin.JointsCount; i++)
            {
                var (joint, _) = skin.GetJoint(i);
                jointNodes.Add(joint.LogicalIndex);
            }
        }

        // Find the first direct child of the prefab root that contains joints
        var prefabRoot = model.LogicalNodes.Count > 0 ? model.LogicalNodes[0] : null;
        if (prefabRoot is null)
        {
            return null;
        }

        foreach (var child in prefabRoot.VisualChildren)
        {
            if (child.Mesh is null && SubtreeContainsAnyJoint(child, jointNodes))
            {
                return child;
            }
        }

        return null;
    }

    private static bool SubtreeContainsAnyJoint(Node node, HashSet<int> jointNodes)
    {
        if (jointNodes.Contains(node.LogicalIndex))
        {
            return true;
        }

        return node.VisualChildren.Any(child => SubtreeContainsAnyJoint(child, jointNodes));
    }

    private static Vector3 SnapScale(Vector3 scale)
    {
        const float tolerance = 1e-5f;
        return new Vector3(
            MathF.Abs(scale.X - 1f) < tolerance ? 1f : scale.X,
            MathF.Abs(scale.Y - 1f) < tolerance ? 1f : scale.Y,
            MathF.Abs(scale.Z - 1f) < tolerance ? 1f : scale.Z);
    }

    private static bool IsUniformSmallScale(Vector3 scale)
    {
        return MathF.Abs(scale.X - scale.Y) < 0.001f
            && MathF.Abs(scale.Y - scale.Z) < 0.001f
            && scale.X > 0.001f
            && scale.X < ContainerScaleThreshold;
    }
}
