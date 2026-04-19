using System.Numerics;
using System.Text.Json.Nodes;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Glb;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;

namespace Jiangyu.Core.Tests.Glb;

public class ModelCleanupSceneTests : IDisposable
{
    private readonly string _tempDir;

    public ModelCleanupSceneTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"jiangyu-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// Static mesh model: LOD containers (small uniform scale) should be
    /// pruned from the clean scene. Normal-scale children should survive.
    /// </summary>
    [Fact]
    public void StaticMesh_LodContainersPruned()
    {
        var glbPath = CreateStaticMeshWithLodContainer();

        var cleanScene = ModelCleanupService.BuildCleanScene(glbPath);
        var cleanModel = cleanScene.ToGltf2();

        var nodeNames = cleanModel.LogicalNodes.Select(n => n.Name).ToHashSet();
        Assert.Contains("PrefabRoot", nodeNames);
        Assert.Contains("TurretMount", nodeNames);
        Assert.DoesNotContain("LODContainer", nodeNames);
    }

    /// <summary>
    /// Static mesh under a scaled container: vertices should be multiplied
    /// by the ancestor container scale factor.
    /// </summary>
    [Fact]
    public void StaticMesh_AncestorScaleBakedIntoVertices()
    {
        var glbPath = CreateStaticMeshUnderScaledContainer(containerScale: 0.01f);

        var cleanScene = ModelCleanupService.BuildCleanScene(glbPath);
        var cleanModel = cleanScene.ToGltf2();

        // The mesh should exist in the output
        Assert.True(cleanModel.LogicalMeshes.Count > 0);

        // The original vertex was at (100, 0, 0). With 0.01 scale baked in,
        // it should now be at approximately (1, 0, 0).
        var mesh = cleanModel.LogicalMeshes[0];
        var positions = mesh.Primitives[0].GetVertexAccessor("POSITION").AsVector3Array();
        var maxX = positions.Max(p => p.X);

        // Container scale 0.01 * vertex position 100 = 1.0
        Assert.True(maxX < 2f, $"Expected scaled vertex X < 2, got {maxX}");
        Assert.True(maxX > 0.5f, $"Expected scaled vertex X > 0.5, got {maxX}");
    }

    /// <summary>
    /// Skinned mesh: the skeleton joint hierarchy must survive cleanup.
    /// </summary>
    [Fact]
    public void SkinnedMesh_SkeletonPreserved()
    {
        var glbPath = CreateSkinnedMeshModel();

        var cleanScene = ModelCleanupService.BuildCleanScene(glbPath);
        var cleanModel = cleanScene.ToGltf2();

        var nodeNames = cleanModel.LogicalNodes.Select(n => n.Name).ToHashSet();
        Assert.Contains("Armature", nodeNames);
        Assert.Contains("Hips", nodeNames);
        Assert.Contains("Spine", nodeNames);
        Assert.Contains("Head", nodeNames);
    }

    /// <summary>
    /// Skinned mesh: container siblings with small scale should not appear
    /// as nodes in the clean scene (they only contribute a scale factor).
    /// </summary>
    [Fact]
    public void SkinnedMesh_ContainerSiblingNotInOutput()
    {
        var glbPath = CreateSkinnedMeshModel();

        var cleanScene = ModelCleanupService.BuildCleanScene(glbPath);
        var cleanModel = cleanScene.ToGltf2();

        var nodeNames = cleanModel.LogicalNodes.Select(n => n.Name).ToHashSet();
        // The container sibling "MeshContainer" (0.01 scale) should not be a node
        Assert.DoesNotContain("MeshContainer", nodeNames);
    }

    /// <summary>
    /// Scale snap: near-1.0 joint scales should be snapped to exactly 1.0.
    /// </summary>
    [Fact]
    public void SkeletonNodes_ScaleSnappedToOne()
    {
        var glbPath = CreateSkinnedMeshModel();

        var cleanScene = ModelCleanupService.BuildCleanScene(glbPath);
        var cleanModel = cleanScene.ToGltf2();

        // All skeleton nodes should have exactly (1,1,1) scale
        // (our fixture uses near-1.0 scales that should be snapped)
        foreach (var node in cleanModel.LogicalNodes)
        {
            if (node.Name is "Armature" or "Hips" or "Spine" or "Head")
            {
                var scale = node.LocalTransform.Scale;
                Assert.Equal(1f, scale.X);
                Assert.Equal(1f, scale.Y);
                Assert.Equal(1f, scale.Z);
            }
        }
    }

    [Fact]
    public void WeightedMeshWithoutSkin_RecoveryAddsSkin()
    {
        var glbPath = CreateWeightedMeshWithoutSkinModel();

        var cleanScene = ModelCleanupService.BuildCleanScene(glbPath);
        var cleanModel = cleanScene.ToGltf2();
        Assert.Empty(cleanModel.LogicalSkins);

        var recovered = AssetPipelineService.FindMissingSkinBindings(cleanModel);
        Assert.NotEmpty(recovered);
        var assignments = AssetPipelineService.CreateRecoveredSkinAssignments(cleanModel, recovered);
        Assert.NotEmpty(assignments);
        Assert.NotEmpty(cleanModel.LogicalSkins);

        var gltfPath = Path.Combine(_tempDir, "recovered_skin_test.gltf");
        cleanModel.SaveGLTF(gltfPath);
        AssetPipelineService.ApplyRecoveredSkinAssignmentsToGltf(gltfPath, assignments);

        var root = JsonNode.Parse(File.ReadAllText(gltfPath))!.AsObject();
        Assert.True((root["skins"] as JsonArray)?.Count > 0);
        Assert.Contains((JsonArray)root["skins"]!, skin => skin is JsonObject obj && obj["inverseBindMatrices"] is not null);
        var nodes = (JsonArray)root["nodes"]!;
        Assert.Contains(nodes, node => node is JsonObject obj && obj["skin"] is not null);
    }

    [Fact]
    public void MissingSkinRecovery_UsesSourceBoneOrderWhenProvided()
    {
        var glbPath = CreateWeightedMeshWithoutSkinModel();

        var cleanScene = ModelCleanupService.BuildCleanScene(glbPath);
        var cleanModel = cleanScene.ToGltf2();
        string RelativePath(Node node)
        {
            var parts = new List<string>();
            var current = node;
            while (current.VisualParent is not null)
            {
                parts.Add(current.Name ?? string.Empty);
                current = current.VisualParent;
            }

            parts.Reverse();
            return string.Join("/", parts);
        }

        var meshNodePath = RelativePath(cleanModel.LogicalNodes.Single(n => n.Mesh?.Name == "weighted_no_skin"));
        var sourceBindings = new[]
        {
            new AssetPipelineService.SourceSkinBinding(
                "weighted_no_skin",
                meshNodePath,
                [
                    RelativePath(cleanModel.LogicalNodes.Single(n => n.Name == "bone2")),
                    RelativePath(cleanModel.LogicalNodes.Single(n => n.Name == "root")),
                    RelativePath(cleanModel.LogicalNodes.Single(n => n.Name == "bone0")),
                ])
        };

        var recovered = AssetPipelineService.FindMissingSkinBindings(cleanModel, sourceBindings);
        Assert.NotEmpty(recovered);

        var first = recovered[0];
        Assert.Equal(3, first.JointNodeIndices.Length);
        var names = first.JointNodeIndices
            .Select(index => cleanModel.LogicalNodes[index].Name)
            .ToArray();

        Assert.Equal(["bone2", "root", "bone0"], names);
    }

    [Fact]
    public void Schema2SkinBinding_AllowsBranchingJointSet()
    {
        var glbPath = CreateWeightedMeshWithoutSkinModel();
        var model = ModelRoot.Load(glbPath);

        var meshNode = model.LogicalNodes.Single(n => n.Name == "WeightedMeshNode");
        var skeletonRoot = model.LogicalNodes.Single(n => n.Name == "root");
        var joints = new List<Node> { skeletonRoot };
        joints.AddRange(EnumerateNodesDepthFirst(skeletonRoot).Where(n => n.LogicalIndex != skeletonRoot.LogicalIndex).Take(12));

        var skin = model.CreateSkin();
        skin.BindJoints(joints.ToArray());
        meshNode.Skin = skin;

        Assert.True(model.LogicalSkins.Count > 0);
        Assert.NotNull(meshNode.Skin);
    }

    // -- Fixture builders --

    private string CreateStaticMeshWithLodContainer()
    {
        var scene = new SceneBuilder();
        var root = new NodeBuilder("PrefabRoot");

        // Normal child — should survive cleanup
        var turret = root.CreateNode("TurretMount");
        turret.LocalTransform = new AffineTransform(Vector3.One, Quaternion.Identity, new Vector3(0, 1, 0));

        // LOD container — 0.01 scale, should be pruned
        var lod = root.CreateNode("LODContainer");
        lod.LocalTransform = new AffineTransform(new Vector3(0.01f, 0.01f, 0.01f), Quaternion.Identity, Vector3.Zero);

        // Attach a static mesh to the turret
        var mesh = BuildTriangleMesh("turret_mesh", Vector3.One);
        scene.AddRigidMesh(mesh, turret);
        scene.AddNode(root);

        var path = Path.Combine(_tempDir, "static_lod.glb");
        scene.ToGltf2().SaveGLB(path);
        return path;
    }

    private string CreateStaticMeshUnderScaledContainer(float containerScale)
    {
        // Build at the Schema2 level for precise node control.
        // SceneBuilder normalises transforms; Schema2 preserves them exactly.
        var model = ModelRoot.CreateModel();
        var modelScene = model.UseScene("default");

        var root = modelScene.CreateNode("PrefabRoot");
        var container = root.CreateNode("ScaledContainer");
        container.LocalTransform = new AffineTransform(
            new Vector3(containerScale, containerScale, containerScale),
            Quaternion.Identity, Vector3.Zero);

        var meshNode = container.CreateNode("MeshNode");

        // Build mesh with a vertex at (100, 0, 0)
        var mesh = model.CreateMesh("scaled_mesh");
        var prim = mesh.CreatePrimitive();
        prim.WithVertexAccessor("POSITION", new[]
        {
            new Vector3(0, 0, 0),
            new Vector3(100, 0, 0),
            new Vector3(0, 100, 0),
        });
        prim.WithVertexAccessor("NORMAL", new[]
        {
            Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ,
        });
        prim.WithVertexAccessor("TEXCOORD_0", new[]
        {
            Vector2.Zero, Vector2.UnitX, Vector2.UnitY,
        });
        prim.WithIndicesAccessor(SharpGLTF.Schema2.PrimitiveType.TRIANGLES, [0, 1, 2]);
        meshNode.Mesh = mesh;

        var path = Path.Combine(_tempDir, "static_scaled.glb");
        model.SaveGLB(path);
        return path;
    }

    private string CreateSkinnedMeshModel()
    {
        // Build at Schema2 level for skinned mesh control
        var model = ModelRoot.CreateModel();
        var modelScene = model.UseScene("default");

        var root = modelScene.CreateNode("PrefabRoot");

        // Skeleton subtree
        var armature = root.CreateNode("Armature");
        armature.LocalTransform = new AffineTransform(
            new Vector3(0.999999f, 1.000001f, 1f), Quaternion.Identity, Vector3.Zero);

        var hips = armature.CreateNode("Hips");
        hips.LocalTransform = new AffineTransform(
            Vector3.One, Quaternion.Identity, new Vector3(0, 1, 0));

        var spine = hips.CreateNode("Spine");
        spine.LocalTransform = new AffineTransform(
            Vector3.One, Quaternion.Identity, new Vector3(0, 0.5f, 0));

        var head = spine.CreateNode("Head");
        head.LocalTransform = new AffineTransform(
            Vector3.One, Quaternion.Identity, new Vector3(0, 0.3f, 0));

        // Container sibling with small scale (holds SkinnedMeshRenderer in Unity)
        var meshContainer = root.CreateNode("MeshContainer");
        meshContainer.LocalTransform = new AffineTransform(
            new Vector3(0.01f, 0.01f, 0.01f), Quaternion.Identity, Vector3.Zero);

        // Build skinned mesh at scene root (like SharpGLTF/AssetRipper places them)
        var mesh = model.CreateMesh("body");
        var prim = mesh.CreatePrimitive();
        prim.WithVertexAccessor("POSITION", new[]
        {
            new Vector3(0, 0, 0),
            new Vector3(1, 0, 0),
            new Vector3(0, 1, 0),
        });
        prim.WithVertexAccessor("NORMAL", new[]
        {
            Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ,
        });
        prim.WithVertexAccessor("TEXCOORD_0", new[]
        {
            Vector2.Zero, Vector2.UnitX, Vector2.UnitY,
        });
        prim.WithVertexAccessor("JOINTS_0", new[]
        {
            new Vector4(0, 0, 0, 0),
            new Vector4(1, 0, 0, 0),
            new Vector4(2, 0, 0, 0),
        });
        prim.WithVertexAccessor("WEIGHTS_0", new[]
        {
            new Vector4(1, 0, 0, 0),
            new Vector4(1, 0, 0, 0),
            new Vector4(1, 0, 0, 0),
        });
        prim.WithIndicesAccessor(SharpGLTF.Schema2.PrimitiveType.TRIANGLES, [0, 1, 2]);

        // Create skin binding to skeleton joints
        var skinNode = modelScene.CreateNode("body_skinned");
        skinNode.Mesh = mesh;
        var skin = model.CreateSkin();
        skin.BindJoints(hips, spine, head);
        skinNode.Skin = skin;

        var path = Path.Combine(_tempDir, "skinned.glb");
        model.SaveGLB(path);
        return path;
    }

    private string CreateWeightedMeshWithoutSkinModel()
    {
        var model = ModelRoot.CreateModel();
        var modelScene = model.UseScene("default");

        var root = modelScene.CreateNode("PrefabRoot");
        var meshParent = root.CreateNode("MeshParent");
        var skeletonRoot = meshParent.CreateNode("root");
        var bone0 = skeletonRoot.CreateNode("bone0");
        var bone1 = bone0.CreateNode("bone1");
        var bone2 = bone1.CreateNode("bone2");

        bone0.LocalTransform = new AffineTransform(Vector3.One, Quaternion.Identity, new Vector3(0, 1, 0));
        bone1.LocalTransform = new AffineTransform(Vector3.One, Quaternion.Identity, new Vector3(0, 0.5f, 0));
        bone2.LocalTransform = new AffineTransform(Vector3.One, Quaternion.Identity, new Vector3(0, 0.3f, 0));

        var mesh = model.CreateMesh("weighted_no_skin");
        var prim = mesh.CreatePrimitive();
        prim.WithVertexAccessor("POSITION", new[]
        {
            new Vector3(0, 0, 0),
            new Vector3(1, 0, 0),
            new Vector3(0, 1, 0),
        });
        prim.WithVertexAccessor("NORMAL", new[]
        {
            Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ,
        });
        prim.WithVertexAccessor("TEXCOORD_0", new[]
        {
            Vector2.Zero, Vector2.UnitX, Vector2.UnitY,
        });
        prim.WithVertexAccessor("JOINTS_0", new[]
        {
            new Vector4(0, 1, 2, 0),
            new Vector4(1, 2, 0, 0),
            new Vector4(2, 1, 0, 0),
        });
        prim.WithVertexAccessor("WEIGHTS_0", new[]
        {
            new Vector4(0.6f, 0.3f, 0.1f, 0f),
            new Vector4(0.5f, 0.5f, 0f, 0f),
            new Vector4(0.7f, 0.3f, 0f, 0f),
        });
        prim.WithIndicesAccessor(SharpGLTF.Schema2.PrimitiveType.TRIANGLES, [0, 1, 2]);

        var meshNode = meshParent.CreateNode("WeightedMeshNode");
        meshNode.Mesh = mesh;

        var path = Path.Combine(_tempDir, "weighted_no_skin.glb");
        model.SaveGLB(path);
        return path;
    }

    private static MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> BuildTriangleMesh(
        string name, Vector3 scale)
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(name);
        var material = new MaterialBuilder("default_mat");
        var prim = mesh.UsePrimitive(material);

        prim.AddTriangle(
            (new VertexPositionNormal(Vector3.Zero * scale, Vector3.UnitY), new VertexTexture1(Vector2.Zero), default),
            (new VertexPositionNormal(Vector3.UnitX * scale, Vector3.UnitY), new VertexTexture1(Vector2.UnitX), default),
            (new VertexPositionNormal(Vector3.UnitY * scale, Vector3.UnitY), new VertexTexture1(Vector2.UnitY), default));

        return mesh;
    }

    private static IEnumerable<Node> EnumerateNodesDepthFirst(Node root)
    {
        yield return root;
        foreach (var child in root.VisualChildren)
        {
            foreach (var descendant in EnumerateNodesDepthFirst(child))
            {
                yield return descendant;
            }
        }
    }
}
