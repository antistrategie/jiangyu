using System.Numerics;
using System.Text.Json.Nodes;
using Jiangyu.Core.Assets;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;

namespace Jiangyu.Core.Tests.Assets;

public class ExportPackageShapeTests : IDisposable
{
    private readonly string _tempDir;

    public ExportPackageShapeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"jiangyu-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void CleanExport_SetsCleanedFlag()
    {
        var scene = BuildSimpleScene();
        var gltfPath = Path.Combine(_tempDir, "model.gltf");

        AssetPipelineService.SaveGltfPackage(scene, [], gltfPath);

        var json = JsonNode.Parse(File.ReadAllText(gltfPath));
        var cleaned = json?["extras"]?["jiangyu"]?["cleaned"]?.GetValue<bool>();
        Assert.True(cleaned);
    }

    [Fact]
    public void CleanExport_SourceMaterialIdentity_StrippedFromOutput()
    {
        // Build a scene with a material that has sourceMaterial in extras
        var material = new MaterialBuilder("body_mat");
        material.Extras = new JsonObject
        {
            ["jiangyu"] = new JsonObject
            {
                ["sourceMaterial"] = new JsonObject
                {
                    ["collection"] = "level0",
                    ["pathId"] = 12345L,
                },
            },
        };

        var scene = BuildSimpleScene(material);
        var gltfPath = Path.Combine(_tempDir, "model.gltf");

        AssetPipelineService.SaveGltfPackage(scene, [], gltfPath);

        // sourceMaterial should be stripped from output
        var json = JsonNode.Parse(File.ReadAllText(gltfPath));
        var materials = json?["materials"]?.AsArray();
        Assert.NotNull(materials);
        Assert.True(materials.Count > 0);

        foreach (var mat in materials)
        {
            var sourceMat = mat?["extras"]?["jiangyu"]?["sourceMaterial"];
            Assert.Null(sourceMat);
        }
    }

    [Fact]
    public void CleanExport_NonStandardTextures_WrittenToDiskAndReferencedInExtras()
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

        var scene = BuildSimpleScene(material);
        var gltfPath = Path.Combine(_tempDir, "model.gltf");

        // Simulate a non-standard texture (_MaskMap is not in StandardChannelMap)
        var fakePng = CreateMinimalPng();
        var textures = new List<AssetPipelineService.DiscoveredTexture>
        {
            new()
            {
                Name = "soldier_MaskMap",
                MaterialName = "body_mat",
                SourceMaterialId = "level0:999",
                Property = "_MaskMap",
                PngData = fakePng,
            },
        };

        AssetPipelineService.SaveGltfPackage(scene, textures, gltfPath);

        // Non-standard texture should be written to textures/ directory
        var texturePath = Path.Combine(_tempDir, "textures", "soldier_MaskMap.png");
        Assert.True(File.Exists(texturePath), "Non-standard texture file should exist on disk");

        // And referenced in material extras
        var json = JsonNode.Parse(File.ReadAllText(gltfPath));
        var matExtras = json?["materials"]?[0]?["extras"]?["jiangyu"]?["textures"];
        Assert.NotNull(matExtras);
        var maskMapPath = matExtras["_MaskMap"]?.GetValue<string>();
        Assert.Equal("textures/soldier_MaskMap.png", maskMapPath);
    }

    [Fact]
    public void CleanExport_StandardTextures_AttachedViaMaterialChannels()
    {
        // Attach a standard-channel texture via MaterialBuilder
        var pngData = CreateMinimalPng();
        var material = new MaterialBuilder("body_mat");
        material.UseChannel("BaseColor")
            .UseTexture()
            .WithPrimaryImage(new MemoryImage(pngData));

        var scene = BuildSimpleScene(material);
        var gltfPath = Path.Combine(_tempDir, "model.gltf");

        AssetPipelineService.SaveGltfPackage(scene, [], gltfPath);

        // The output .gltf should have a PBR baseColorTexture reference
        var json = JsonNode.Parse(File.ReadAllText(gltfPath));
        var pbr = json?["materials"]?[0]?["pbrMetallicRoughness"];
        Assert.NotNull(pbr?["baseColorTexture"]);
    }

    [Fact]
    public void CleanExport_NoTextures_ProducesValidGltf()
    {
        var scene = BuildSimpleScene();
        var gltfPath = Path.Combine(_tempDir, "model.gltf");

        AssetPipelineService.SaveGltfPackage(scene, [], gltfPath);

        Assert.True(File.Exists(gltfPath));
        var json = JsonNode.Parse(File.ReadAllText(gltfPath));
        Assert.NotNull(json?["asset"]);
        Assert.NotNull(json?["extras"]?["jiangyu"]?["cleaned"]);
    }

    // -- Helpers --

    private static SceneBuilder BuildSimpleScene(MaterialBuilder? material = null)
    {
        material ??= new MaterialBuilder("default_mat");

        var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>("test_mesh");
        var prim = mesh.UsePrimitive(material);
        prim.AddTriangle(
            (new VertexPositionNormal(Vector3.Zero, Vector3.UnitY), new VertexTexture1(Vector2.Zero), default),
            (new VertexPositionNormal(Vector3.UnitX, Vector3.UnitY), new VertexTexture1(Vector2.UnitX), default),
            (new VertexPositionNormal(Vector3.UnitY, Vector3.UnitY), new VertexTexture1(Vector2.UnitY), default));

        var scene = new SceneBuilder();
        var node = new NodeBuilder("Root");
        scene.AddRigidMesh(mesh, node);
        return scene;
    }

    /// <summary>
    /// Creates a minimal valid PNG (1x1 white pixel) for texture tests.
    /// </summary>
    private static byte[] CreateMinimalPng()
    {
        // Minimal 1x1 white PNG
        return [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR chunk
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
            0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41, // IDAT chunk
            0x54, 0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0x00,
            0x00, 0x00, 0x02, 0x00, 0x01, 0xE2, 0x21, 0xBC,
            0x33, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, // IEND chunk
            0x44, 0xAE, 0x42, 0x60, 0x82,
        ];
    }
}
