using System.Numerics;
using System.Text.Json.Nodes;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Glb;
using Jiangyu.Core.Tests.Helpers;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;

namespace Jiangyu.Core.Tests.Assets;

public sealed class ExportPipelineIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public ExportPipelineIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"jiangyu-export-integration-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void CleanExportPipeline_PreservesStandardChannels_AndWritesNonStandardExtras()
    {
        var rawGlbPath = Path.Combine(_tempDir, "raw.glb");
        WriteRawSourceGlb(rawGlbPath);

        var materialTextures = new Dictionary<string, List<(string channelKey, byte[] pngData)>>(StringComparer.Ordinal)
        {
            ["level0:999"] =
            [
                ("BaseColor", CreateMinimalPng()),
            ],
        };

        var cleanScene = ModelCleanupService.BuildCleanScene(rawGlbPath, new NullLogSink(), materialTextures);
        var gltfPath = Path.Combine(_tempDir, "model.gltf");
        var nonStandardTextures = new List<AssetPipelineService.DiscoveredTexture>
        {
            new()
            {
                Name = "soldier_MaskMap",
                MaterialName = "body_mat",
                SourceMaterialId = "level0:999",
                Property = "_MaskMap",
                PngData = CreateMinimalPng(),
            },
        };

        AssetPipelineService.SaveGltfPackage(cleanScene, nonStandardTextures, gltfPath);

        var json = JsonNode.Parse(File.ReadAllText(gltfPath));
        Assert.True(json?["extras"]?["jiangyu"]?["cleaned"]?.GetValue<bool>());
        Assert.NotNull(json?["materials"]?[0]?["pbrMetallicRoughness"]?["baseColorTexture"]);
        Assert.Equal(
            "textures/soldier_MaskMap.png",
            json?["materials"]?[0]?["extras"]?["jiangyu"]?["textures"]?["_MaskMap"]?.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(_tempDir, "textures", "soldier_MaskMap.png")));
    }

    private static void WriteRawSourceGlb(string path)
    {
        var material = new MaterialBuilder("body_mat");
        material.Extras = new JsonObject
        {
            ["jiangyu"] = new JsonObject
            {
                ["sourceMaterial"] = new JsonObject
                {
                    ["collection"] = "level0",
                    ["pathId"] = 999L,
                },
            },
        };

        var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>("body_mesh");
        var prim = mesh.UsePrimitive(material);
        prim.AddTriangle(
            (new VertexPositionNormal(Vector3.Zero, Vector3.UnitY), new VertexTexture1(Vector2.Zero), default),
            (new VertexPositionNormal(Vector3.UnitX, Vector3.UnitY), new VertexTexture1(Vector2.UnitX), default),
            (new VertexPositionNormal(Vector3.UnitY, Vector3.UnitY), new VertexTexture1(Vector2.UnitY), default));

        var scene = new SceneBuilder();
        var root = new NodeBuilder("PrefabRoot");
        scene.AddRigidMesh(mesh, root);
        scene.AddNode(root);
        scene.ToGltf2().SaveGLB(path);
    }

    private static byte[] CreateMinimalPng()
    {
        return
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
            0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41,
            0x54, 0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0x00,
            0x00, 0x00, 0x02, 0x00, 0x01, 0xE2, 0x21, 0xBC,
            0x33, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E,
            0x44, 0xAE, 0x42, 0x60, 0x82,
        ];
    }
}
