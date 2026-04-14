using Jiangyu.Core.Models;

namespace Jiangyu.Core.Tests.Models;

public class ModManifestTests
{
    [Fact]
    public void CreateDefault_SetsNameAndDefaults()
    {
        var manifest = ModManifest.CreateDefault("TestMod");

        Assert.Equal("TestMod", manifest.Name);
        Assert.Equal("0.1.0", manifest.Version);
        Assert.NotNull(manifest.Depends);
        Assert.Single(manifest.Depends);
        Assert.Contains("Jiangyu >= 1.0.0", manifest.Depends);
    }

    [Fact]
    public void Roundtrip_PreservesAllFields()
    {
        var original = new ModManifest
        {
            Name = "My Mod",
            Version = "1.2.3",
            Author = "Test Author",
            Description = "A test mod",
            Depends = ["Jiangyu >= 1.0.0", "OtherMod >= 2.0"],
        };

        var json = original.ToJson();
        var restored = ModManifest.FromJson(json);

        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.Version, restored.Version);
        Assert.Equal(original.Author, restored.Author);
        Assert.Equal(original.Description, restored.Description);
        Assert.Equal(original.Depends, restored.Depends);
    }

    [Fact]
    public void FromJson_MinimalJson_NullableFieldsDefaultToNull()
    {
        var json = """{"name": "Minimal"}""";
        var manifest = ModManifest.FromJson(json);

        Assert.Equal("Minimal", manifest.Name);
        Assert.Null(manifest.Author);
        Assert.Null(manifest.Description);
        Assert.Null(manifest.Meshes);
    }

    [Fact]
    public void MeshEntry_StringShorthand_ParsesAsSource()
    {
        var json = """
        {
            "name": "Test",
            "meshes": {
                "target_mesh": "models/soldier.glb#body"
            }
        }
        """;

        var manifest = ModManifest.FromJson(json);

        Assert.NotNull(manifest.Meshes);
        Assert.True(manifest.Meshes.ContainsKey("target_mesh"));
        Assert.Equal("models/soldier.glb#body", manifest.Meshes["target_mesh"].Source);
        Assert.Null(manifest.Meshes["target_mesh"].Compiled);
    }

    [Fact]
    public void MeshEntry_ObjectForm_ParsesSourceAndCompiled()
    {
        var json = """
        {
            "name": "Test",
            "meshes": {
                "target_mesh": {
                    "source": "models/soldier.glb#body",
                    "compiled": {
                        "boneNames": ["Hips", "Spine", "Head"],
                        "materials": [
                            {
                                "slot": 0,
                                "name": "Body",
                                "textures": {
                                    "_BaseColorMap": "soldier_body_BaseMap",
                                    "_NormalMap": "soldier_body_NormalMap"
                                }
                            }
                        ]
                    }
                }
            }
        }
        """;

        var manifest = ModManifest.FromJson(json);
        var entry = manifest.Meshes!["target_mesh"];

        Assert.Equal("models/soldier.glb#body", entry.Source);
        Assert.NotNull(entry.Compiled);
        Assert.Equal(["Hips", "Spine", "Head"], entry.Compiled.BoneNames);
        var material = Assert.Single(entry.Compiled.Materials!);
        Assert.Equal(0, material.Slot);
        Assert.Equal("Body", material.Name);
        Assert.Equal("soldier_body_BaseMap", material.Textures["_BaseColorMap"]);
        Assert.Equal("soldier_body_NormalMap", material.Textures["_NormalMap"]);
    }

    [Fact]
    public void MeshEntry_WithoutCompiled_SerialisesAsString()
    {
        var manifest = new ModManifest
        {
            Name = "Test",
            Meshes = new()
            {
                ["target"] = new MeshManifestEntry { Source = "models/file.glb" },
            },
        };

        var json = manifest.ToJson();

        Assert.Contains("\"models/file.glb\"", json);
        Assert.DoesNotContain("\"source\"", json);
    }

    [Fact]
    public void MeshEntry_WithCompiled_SerialisesAsObject()
    {
        var manifest = new ModManifest
        {
            Name = "Test",
            Meshes = new()
            {
                ["target"] = new MeshManifestEntry
                {
                    Source = "models/file.glb",
                    Compiled = new CompiledMeshMetadata
                    {
                        BoneNames = ["Hips"],
                        Materials =
                        [
                            new CompiledMaterialBinding
                            {
                                Slot = 0,
                                Name = "Body",
                                Textures = new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["_BaseColorMap"] = "body_BaseMap",
                                },
                            },
                        ],
                    },
                },
            },
        };

        var json = manifest.ToJson();

        Assert.Contains("\"source\"", json);
        Assert.Contains("\"compiled\"", json);
        Assert.Contains("\"boneNames\"", json);
        Assert.Contains("\"materials\"", json);
    }

    [Fact]
    public void MeshEntry_CompiledRoundtrip_PreservesBoneNamesAndMaterialBindings()
    {
        var original = new ModManifest
        {
            Name = "Test",
            Meshes = new()
            {
                ["target"] = new MeshManifestEntry
                {
                    Source = "models/file.glb",
                    Compiled = new CompiledMeshMetadata
                    {
                        BoneNames = ["Hips", "Spine", "Head"],
                        Materials =
                        [
                            new CompiledMaterialBinding
                            {
                                Slot = 1,
                                Name = "Eyes",
                                Textures = new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["_BaseColorMap"] = "eyes_BaseMap",
                                },
                            },
                        ],
                    },
                },
            },
        };

        var json = original.ToJson();
        var restored = ModManifest.FromJson(json);
        var entry = restored.Meshes!["target"];

        Assert.Equal("models/file.glb", entry.Source);
        Assert.Equal(["Hips", "Spine", "Head"], entry.Compiled!.BoneNames);
        var material = Assert.Single(entry.Compiled.Materials!);
        Assert.Equal(1, material.Slot);
        Assert.Equal("Eyes", material.Name);
        Assert.Equal("eyes_BaseMap", material.Textures["_BaseColorMap"]);
    }
}
