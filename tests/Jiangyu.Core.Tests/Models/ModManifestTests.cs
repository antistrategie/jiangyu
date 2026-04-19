using Jiangyu.Core.Models;
using Jiangyu.Shared.Templates;

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
        Assert.Null(manifest.TemplatePatches);
    }

    [Fact]
    public void TemplatePatches_Roundtrip_PreservesTypedScalarValues()
    {
        var original = new ModManifest
        {
            Name = "Test",
            TemplatePatches =
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "EntityTemplate",
                    TemplateId = "local_forces_basic_soldier",
                    Set =
                    [
                        new CompiledTemplateSetOperation
                        {
                            FieldPath = "CanBeDowned",
                            Value = new CompiledTemplateValue
                            {
                                Kind = CompiledTemplateValueKind.Boolean,
                                Boolean = true,
                            },
                        },
                        new CompiledTemplateSetOperation
                        {
                            FieldPath = "DeploymentCost",
                            Value = new CompiledTemplateValue
                            {
                                Kind = CompiledTemplateValueKind.Int32,
                                Int32 = 7,
                            },
                        },
                        new CompiledTemplateSetOperation
                        {
                            FieldPath = "CharacterHeight",
                            Value = new CompiledTemplateValue
                            {
                                Kind = CompiledTemplateValueKind.Single,
                                Single = 1.25f,
                            },
                        },
                        new CompiledTemplateSetOperation
                        {
                            FieldPath = "DebugName",
                            Value = new CompiledTemplateValue
                            {
                                Kind = CompiledTemplateValueKind.String,
                                String = "patched",
                            },
                        },
                        new CompiledTemplateSetOperation
                        {
                            FieldPath = "ActorType",
                            Value = new CompiledTemplateValue
                            {
                                Kind = CompiledTemplateValueKind.Enum,
                                EnumType = "EntityTemplateActorType",
                                EnumValue = "Human",
                            },
                        },
                    ],
                },
            ],
        };

        var json = original.ToJson();
        var restored = ModManifest.FromJson(json);

        var patch = Assert.Single(restored.TemplatePatches!);
        Assert.Equal("EntityTemplate", patch.TemplateType);
        Assert.Equal("local_forces_basic_soldier", patch.TemplateId);
        Assert.Equal(5, patch.Set.Count);

        Assert.Equal(CompiledTemplateValueKind.Boolean, patch.Set[0].Value!.Kind);
        Assert.True(patch.Set[0].Value!.Boolean);

        Assert.Equal(CompiledTemplateValueKind.Int32, patch.Set[1].Value!.Kind);
        Assert.Equal(7, patch.Set[1].Value!.Int32);

        Assert.Equal(CompiledTemplateValueKind.Single, patch.Set[2].Value!.Kind);
        Assert.Equal(1.25f, patch.Set[2].Value!.Single);

        Assert.Equal(CompiledTemplateValueKind.String, patch.Set[3].Value!.Kind);
        Assert.Equal("patched", patch.Set[3].Value!.String);

        Assert.Equal(CompiledTemplateValueKind.Enum, patch.Set[4].Value!.Kind);
        Assert.Equal("EntityTemplateActorType", patch.Set[4].Value!.EnumType);
        Assert.Equal("Human", patch.Set[4].Value!.EnumValue);
    }

    [Fact]
    public void TemplatePatches_Roundtrip_PreservesTemplateTypePerEntry()
    {
        var original = new ModManifest
        {
            Name = "Multi",
            TemplatePatches =
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "EntityTemplate",
                    TemplateId = "player_squad.darby",
                    Set =
                    [
                        new CompiledTemplateSetOperation
                        {
                            FieldPath = "HudYOffsetScale",
                            Value = new CompiledTemplateValue
                            {
                                Kind = CompiledTemplateValueKind.Single,
                                Single = 5.0f,
                            },
                        },
                    ],
                },
                new CompiledTemplatePatch
                {
                    TemplateType = "UnitLeaderTemplate",
                    TemplateId = "squad_leader_test",
                    Set =
                    [
                        new CompiledTemplateSetOperation
                        {
                            FieldPath = "PromotionTax",
                            Value = new CompiledTemplateValue
                            {
                                Kind = CompiledTemplateValueKind.Int32,
                                Int32 = 1,
                            },
                        },
                    ],
                },
            ],
        };

        var restored = ModManifest.FromJson(original.ToJson());

        Assert.NotNull(restored.TemplatePatches);
        Assert.Equal(2, restored.TemplatePatches!.Count);
        Assert.Equal("EntityTemplate", restored.TemplatePatches[0].TemplateType);
        Assert.Equal("UnitLeaderTemplate", restored.TemplatePatches[1].TemplateType);
    }

    [Fact]
    public void TemplatePatches_FromJson_OmittedTemplateType_DeserialisesAsNull()
    {
        var json = """
        {
            "name": "OmittedType",
            "templatePatches": [
                {
                    "templateId": "some_template",
                    "set": [
                        { "fieldPath": "Field", "value": { "kind": "Boolean", "boolean": true } }
                    ]
                }
            ]
        }
        """;

        var manifest = ModManifest.FromJson(json);
        var patch = Assert.Single(manifest.TemplatePatches!);
        Assert.Null(patch.TemplateType);
        Assert.Equal("some_template", patch.TemplateId);
    }

    [Fact]
    public void TemplatePatches_FromJson_IndexedFieldPath_Deserialises()
    {
        var json = """
        {
            "name": "Indexed",
            "templatePatches": [
                {
                    "templateType": "EntityTemplate",
                    "templateId": "foo",
                    "set": [
                        { "fieldPath": "Properties.MoraleEvents[2].SomeField",
                          "value": { "kind": "Int32", "int32": 42 } }
                    ]
                }
            ]
        }
        """;

        var manifest = ModManifest.FromJson(json);
        var op = Assert.Single(manifest.TemplatePatches![0].Set);
        Assert.Equal("Properties.MoraleEvents[2].SomeField", op.FieldPath);
        Assert.Equal(42, op.Value!.Int32);
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
