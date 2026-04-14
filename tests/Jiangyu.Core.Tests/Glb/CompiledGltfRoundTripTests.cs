using System.Numerics;
using System.Text.Json.Nodes;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Glb;
using Jiangyu.Core.Models;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;
using SharpGLTF.Scenes;

namespace Jiangyu.Core.Tests.Glb;

public sealed class CompiledGltfRoundTripTests : IDisposable
{
    private readonly string _tempDir;

    public CompiledGltfRoundTripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"jiangyu-roundtrip-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void SavedGltfPackage_RoundTripsCompilerTextureDiscovery_AndCleanedFlag()
    {
        var gltfPath = Path.Combine(_tempDir, "model.gltf");
        WriteCompiledLikeGltfPackage(gltfPath);

        var textures = new Dictionary<string, GlbMeshBundleCompiler.CompiledTexture>(StringComparer.Ordinal);

        var found = GlbMeshBundleCompiler.TryExtractTexturesFromGltf(gltfPath, textures);

        Assert.True(found);
        Assert.True(GlbMeshBundleCompiler.IsCleanedExport(gltfPath));
        Assert.Equal("soldier_BaseMap", GlbMeshBundleCompiler.InferTexturePrefix(gltfPath));
        Assert.Contains("soldier_BaseMap", textures.Keys);
        Assert.Contains("soldier_MaskMap", textures.Keys);
    }

    [Fact]
    public void BuildMaterialBindings_UsesExplicitMaterialSlotTextureMappings()
    {
        var gltfPath = Path.Combine(_tempDir, "bindings.gltf");
        WriteMaterialBindingsGltfPackage(gltfPath);

        var bindings = GlbMeshBundleCompiler.BuildMaterialBindings(
            [
                new GlbMeshBundleCompiler.MeshSourceEntry
                {
                    SourceFilePath = gltfPath,
                    BundleMeshName = "body_mesh",
                    HasExplicitMeshName = true,
                },
            ]);

        var meshBindings = bindings["body_mesh"];
        Assert.Equal(2, meshBindings.Count);

        Assert.Equal(0, meshBindings[0].Slot);
        Assert.Equal("Body", meshBindings[0].Name);
        Assert.Equal("body_BaseMap", meshBindings[0].Textures["_BaseColorMap"]);
        Assert.Equal("body_NormalMap", meshBindings[0].Textures["_NormalMap"]);
        Assert.Equal("body_MaskMap", meshBindings[0].Textures["_MaskMap"]);

        Assert.Equal(1, meshBindings[1].Slot);
        Assert.Equal("Eyes", meshBindings[1].Name);
        Assert.Equal("eyes_BaseMap", meshBindings[1].Textures["_BaseColorMap"]);
        Assert.Equal("eyes_EffectMap", meshBindings[1].Textures["_Effect_Map"]);
    }

    private static void WriteCompiledLikeGltfPackage(string gltfPath)
    {
        var png = CreateMinimalPng();
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
        material.UseChannel("BaseColor")
            .UseTexture()
            .WithPrimaryImage(new MemoryImage(png));
        material.UseChannel("Normal")
            .UseTexture()
            .WithPrimaryImage(new MemoryImage(png));

        var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>("body_mesh");
        var prim = mesh.UsePrimitive(material);
        prim.AddTriangle(
            (new VertexPositionNormal(Vector3.Zero, Vector3.UnitY), new VertexTexture1(Vector2.Zero), default),
            (new VertexPositionNormal(Vector3.UnitX, Vector3.UnitY), new VertexTexture1(Vector2.UnitX), default),
            (new VertexPositionNormal(Vector3.UnitY, Vector3.UnitY), new VertexTexture1(Vector2.UnitY), default));

        var scene = new SceneBuilder();
        var root = new NodeBuilder("Root");
        scene.AddRigidMesh(mesh, root);
        scene.AddNode(root);

        var textures = new List<AssetPipelineService.DiscoveredTexture>
        {
            new()
            {
                Name = "soldier_BaseMap",
                MaterialName = "body_mat",
                SourceMaterialId = "level0:999",
                Property = "_BaseColorMap",
                PngData = png,
            },
            new()
            {
                Name = "soldier_MaskMap",
                MaterialName = "body_mat",
                SourceMaterialId = "level0:999",
                Property = "_MaskMap",
                PngData = png,
            },
            new()
            {
                Name = "soldier_NormalMap",
                MaterialName = "body_mat",
                SourceMaterialId = "level0:999",
                Property = "_NormalMap",
                PngData = png,
            },
        };

        AssetPipelineService.SaveGltfPackage(scene, textures, gltfPath);
    }

    private static void WriteMaterialBindingsGltfPackage(string gltfPath)
    {
        var bodyBase = CreateTaggedPng(0x01);
        var bodyNormal = CreateTaggedPng(0x02);
        var eyesBase = CreateTaggedPng(0x03);

        var body = new MaterialBuilder("Body");
        body.UseChannel("BaseColor")
            .UseTexture()
            .WithPrimaryImage(new MemoryImage(bodyBase));
        body.UseChannel("Normal")
            .UseTexture()
            .WithPrimaryImage(new MemoryImage(bodyNormal));
        body.Extras = new JsonObject
        {
            ["jiangyu"] = new JsonObject
            {
                ["textures"] = new JsonObject
                {
                    ["_MaskMap"] = "textures/body_MaskMap.png",
                },
            },
        };

        var eyes = new MaterialBuilder("Eyes");
        eyes.UseChannel("BaseColor")
            .UseTexture()
            .WithPrimaryImage(new MemoryImage(eyesBase));
        eyes.Extras = new JsonObject
        {
            ["jiangyu"] = new JsonObject
            {
                ["textures"] = new JsonObject
                {
                    ["_Effect_Map"] = "textures/eyes_EffectMap.png",
                },
            },
        };

        var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>("body_mesh");
        var bodyPrim = mesh.UsePrimitive(body);
        bodyPrim.AddTriangle(
            (new VertexPositionNormal(Vector3.Zero, Vector3.UnitY), new VertexTexture1(Vector2.Zero), default),
            (new VertexPositionNormal(Vector3.UnitX, Vector3.UnitY), new VertexTexture1(Vector2.UnitX), default),
            (new VertexPositionNormal(Vector3.UnitY, Vector3.UnitY), new VertexTexture1(Vector2.UnitY), default));

        var eyesPrim = mesh.UsePrimitive(eyes);
        eyesPrim.AddTriangle(
            (new VertexPositionNormal(new Vector3(0, 0, 1), Vector3.UnitY), new VertexTexture1(Vector2.Zero), default),
            (new VertexPositionNormal(new Vector3(1, 0, 1), Vector3.UnitY), new VertexTexture1(Vector2.UnitX), default),
            (new VertexPositionNormal(new Vector3(0, 1, 1), Vector3.UnitY), new VertexTexture1(Vector2.UnitY), default));

        var scene = new SceneBuilder();
        var root = new NodeBuilder("Root");
        scene.AddRigidMesh(mesh, root);
        scene.AddNode(root);

        var model = scene.ToGltf2();
        AssignImageName(model, "Body", "BaseColor", "body_BaseMap");
        AssignImageName(model, "Body", "Normal", "body_NormalMap");
        AssignImageName(model, "Eyes", "BaseColor", "eyes_BaseMap");

        var texturesDir = Path.Combine(Path.GetDirectoryName(gltfPath)!, "textures");
        Directory.CreateDirectory(texturesDir);
        File.WriteAllBytes(Path.Combine(texturesDir, "body_MaskMap.png"), CreateMinimalPng());
        File.WriteAllBytes(Path.Combine(texturesDir, "eyes_EffectMap.png"), CreateMinimalPng());

        model.SaveGLTF(gltfPath, new WriteSettings { ImageWriting = ResourceWriteMode.SatelliteFile });
    }

    private static void AssignImageName(ModelRoot model, string materialName, string channelKey, string imageName)
    {
        var material = Assert.Single(model.LogicalMaterials, m => m.Name == materialName);
        var channel = Assert.Single(material.Channels, c => c.Key == channelKey);
        channel.Texture!.PrimaryImage!.Name = imageName;
        channel.Texture.PrimaryImage.AlternateWriteFileName = $"textures/{imageName}.png";
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

    private static byte[] CreateTaggedPng(byte tag)
    {
        var bytes = CreateMinimalPng().ToArray();
        Array.Resize(ref bytes, bytes.Length + 1);
        bytes[^1] = tag;
        return bytes;
    }
}
