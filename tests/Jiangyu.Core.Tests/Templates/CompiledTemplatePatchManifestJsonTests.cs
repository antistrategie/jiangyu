using System.Text.Json;
using Jiangyu.Shared.Templates;
using static Jiangyu.Core.Tests.Templates.CompiledTemplateTestHelpers;

namespace Jiangyu.Core.Tests.Templates;

/// <summary>
/// Wire-format round-trip tests for the compiled patch manifest. The manifest
/// is the contract between the compiler and the loader; both ends use
/// System.Text.Json with the same DTOs, so a round-trip from one shape to
/// JSON and back must preserve every field. These tests guard against silent
/// drift in JSON property names or default-value handling — the kind of
/// regression that would break compiled mods on a loader update.
/// </summary>
public class CompiledTemplatePatchManifestJsonTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
    };

    [Fact]
    public void Descent_RoundTrip()
    {
        var op = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Set,
            FieldPath = "ShowHUDText",
            Descent =
            [
                new TemplateDescentStep { Field = "EventHandlers", Index = 0 },
            ],
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Boolean,
                Boolean = true,
            },
        };

        var json = JsonSerializer.Serialize(op, Options);
        Assert.Contains("\"descent\"", json);
        Assert.Contains("\"field\":\"EventHandlers\"", json);
        Assert.Contains("\"index\":0", json);

        var roundTripped = JsonSerializer.Deserialize<CompiledTemplateSetOperation>(json, Options);
        Assert.NotNull(roundTripped);
        Assert.Equal("ShowHUDText", roundTripped!.FieldPath);
        Assert.NotNull(roundTripped.Descent);
        Assert.Single(roundTripped.Descent);
        Assert.Equal("EventHandlers", roundTripped.Descent[0].Field);
        Assert.Equal(0, roundTripped.Descent[0].Index);
    }

    [Fact]
    public void Descent_MultipleSteps_RoundTrip()
    {
        // Multi-level descent: descent steps at distinct positions need to
        // survive the JSON round-trip with both entries intact.
        var op = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Set,
            FieldPath = "Leaf",
            Descent =
            [
                new TemplateDescentStep { Field = "Outer", Index = 0 },
                new TemplateDescentStep { Field = "Inner", Index = 2 },
            ],
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Int32,
                Int32 = 5,
            },
        };

        var json = JsonSerializer.Serialize(op, Options);
        var roundTripped = JsonSerializer.Deserialize<CompiledTemplateSetOperation>(json, Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(2, roundTripped!.Descent!.Count);
        Assert.Equal("Outer", roundTripped.Descent[0].Field);
        Assert.Equal(0, roundTripped.Descent[0].Index);
        Assert.Equal("Inner", roundTripped.Descent[1].Field);
        Assert.Equal(2, roundTripped.Descent[1].Index);
    }

    [Fact]
    public void NullDescent_OmittedFromJson()
    {
        // The wire format optimises for the common case (no descent through
        // a polymorphic boundary) by omitting null Descent entirely from the
        // serialised output. Keeps compiled bundles small.
        var op = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Set,
            FieldPath = "HudYOffsetScale",
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Single,
                Single = 2.0f,
            },
        };

        var json = JsonSerializer.Serialize(op, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });

        Assert.DoesNotContain("descent", json);

        var roundTripped = JsonSerializer.Deserialize<CompiledTemplateSetOperation>(json, Options);
        Assert.Null(roundTripped!.Descent);
    }

    [Fact]
    public void TypeConstruction_RoundTrip()
    {
        var op = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Append,
            FieldPath = "EventHandlers",
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.TypeConstruction,
                TypeConstruction = new CompiledTemplateComposite
                {
                    TypeName = "AddSkill",
                    Operations = SetOps(
                        ("Event", new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.Enum,
                            EnumType = "AddEvent",
                            EnumValue = "OnAttack",
                        }),
                        ("ShowHUDText", new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.Boolean,
                            Boolean = true,
                        })),
                },
            },
        };

        var json = JsonSerializer.Serialize(op, Options);
        Assert.Contains("\"TypeConstruction\"", json);
        Assert.Contains("\"typeConstruction\"", json);
        Assert.Contains("\"AddSkill\"", json);
        Assert.Contains("\"OnAttack\"", json);

        var roundTripped = JsonSerializer.Deserialize<CompiledTemplateSetOperation>(json, Options);
        Assert.NotNull(roundTripped);
        Assert.Equal(CompiledTemplateValueKind.TypeConstruction, roundTripped!.Value!.Kind);
        Assert.NotNull(roundTripped.Value.TypeConstruction);
        Assert.Equal("AddSkill", roundTripped.Value.TypeConstruction!.TypeName);
        Assert.Equal(2, roundTripped.Value.TypeConstruction.Operations.Count);
        Assert.Equal("OnAttack", roundTripped.Value.TypeConstruction.ValueAt("Event").EnumValue);
        Assert.True(roundTripped.Value.TypeConstruction.ValueAt("ShowHUDText").Boolean);
    }

    [Fact]
    public void TypeConstruction_AndDescent_CoexistInOneOp()
    {
        // A "replace element N with new construction" op can carry both
        // Descent (in case the path descends through a polymorphic
        // intermediate; rare) and a TypeConstruction value at the
        // terminal. Both must round-trip together.
        var op = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Set,
            FieldPath = "EventHandlers",
            Index = 1,
            Descent =
            [
                new TemplateDescentStep { Field = "Outer", Index = 0 },
            ],
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.TypeConstruction,
                TypeConstruction = new CompiledTemplateComposite
                {
                    TypeName = "ChangeProperty",
                    Operations = SetOps(
                        ("Amount", new() { Kind = CompiledTemplateValueKind.Int32, Int32 = 5 })),
                },
            },
        };

        var json = JsonSerializer.Serialize(op, Options);
        var roundTripped = JsonSerializer.Deserialize<CompiledTemplateSetOperation>(json, Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(1, roundTripped!.Index);
        Assert.NotNull(roundTripped.Descent);
        Assert.Single(roundTripped.Descent);
        Assert.Equal("Outer", roundTripped.Descent[0].Field);
        Assert.Equal(0, roundTripped.Descent[0].Index);
        Assert.Equal("ChangeProperty", roundTripped.Value!.TypeConstruction!.TypeName);
    }

    [Fact]
    public void AssetReference_RoundTrip()
    {
        // The asset reference is the foundation for the future prefab-cloning
        // template-redirection step, so its wire shape must stay stable across
        // compiler/loader updates: a value kind discriminator plus a single
        // string name. The category is derived at apply time from the
        // destination field's declared Unity type, never carried on the wire.
        var op = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Set,
            FieldPath = "Icon",
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.AssetReference,
                Asset = new CompiledAssetReference { Name = "item/fancy-pen-icon" },
            },
        };

        var json = JsonSerializer.Serialize(op, Options);
        Assert.Contains("\"AssetReference\"", json);
        Assert.Contains("\"asset\"", json);
        Assert.Contains("\"name\":\"item/fancy-pen-icon\"", json);

        var roundTripped = JsonSerializer.Deserialize<CompiledTemplateSetOperation>(json, Options);
        Assert.NotNull(roundTripped);
        Assert.Equal(CompiledTemplateValueKind.AssetReference, roundTripped!.Value!.Kind);
        Assert.NotNull(roundTripped.Value.Asset);
        Assert.Equal("item/fancy-pen-icon", roundTripped.Value.Asset!.Name);
    }

    [Fact]
    public void CompositeAndTypeConstruction_AreDistinctValueKinds()
    {
        // Sanity: two value kinds with the same payload type must serialise
        // distinguishably so the runtime applier dispatches on the right
        // construction path. A regression here would cause inline-composite
        // values to take the ScriptableObject construction path or vice
        // versa.
        var compositeOp = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Append,
            FieldPath = "Perks",
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Composite,
                Composite = new CompiledTemplateComposite { TypeName = "Perk", Operations = SetOps(("Tier", new() { Kind = CompiledTemplateValueKind.Int32, Int32 = 1 })) },
            },
        };
        var handlerOp = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Append,
            FieldPath = "EventHandlers",
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.TypeConstruction,
                TypeConstruction = new CompiledTemplateComposite { TypeName = "AddSkill", Operations = SetOps(("ShowHUDText", new() { Kind = CompiledTemplateValueKind.Boolean, Boolean = true })) },
            },
        };

        var compositeJson = JsonSerializer.Serialize(compositeOp, Options);
        var handlerJson = JsonSerializer.Serialize(handlerOp, Options);

        Assert.Contains("\"Composite\"", compositeJson);
        Assert.DoesNotContain("\"TypeConstruction\"", compositeJson);
        Assert.Contains("\"TypeConstruction\"", handlerJson);
        Assert.DoesNotContain("\"Composite\"", handlerJson);
    }

    [Fact]
    public void Manifest_Roundtrip_PreservesTypedScalarValues()
    {
        var original = new CompiledTemplatePatchManifest
        {
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
                            Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Boolean, Boolean = true },
                        },
                        new CompiledTemplateSetOperation
                        {
                            FieldPath = "DeploymentCost",
                            Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = 7 },
                        },
                        new CompiledTemplateSetOperation
                        {
                            FieldPath = "CharacterHeight",
                            Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Single, Single = 1.25f },
                        },
                        new CompiledTemplateSetOperation
                        {
                            FieldPath = "DebugName",
                            Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.String, String = "patched" },
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

        var restored = CompiledTemplatePatchManifest.FromJson(original.ToJson());

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
    public void Manifest_Roundtrip_PreservesTemplateTypePerEntry()
    {
        var original = new CompiledTemplatePatchManifest
        {
            TemplatePatches =
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "EntityTemplate",
                    TemplateId = "player_squad.darby",
                    Set = [new CompiledTemplateSetOperation { FieldPath = "HudYOffsetScale", Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Single, Single = 5.0f } }],
                },
                new CompiledTemplatePatch
                {
                    TemplateType = "UnitLeaderTemplate",
                    TemplateId = "squad_leader_test",
                    Set = [new CompiledTemplateSetOperation { FieldPath = "PromotionTax", Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = 1 } }],
                },
            ],
        };

        var restored = CompiledTemplatePatchManifest.FromJson(original.ToJson());

        Assert.NotNull(restored.TemplatePatches);
        Assert.Equal(2, restored.TemplatePatches!.Count);
        Assert.Equal("EntityTemplate", restored.TemplatePatches[0].TemplateType);
        Assert.Equal("UnitLeaderTemplate", restored.TemplatePatches[1].TemplateType);
    }

    [Fact]
    public void Manifest_FromJson_OmittedTemplateType_DeserialisesAsNull()
    {
        var json = """
        {
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

        var manifest = CompiledTemplatePatchManifest.FromJson(json);
        var patch = Assert.Single(manifest.TemplatePatches!);
        Assert.Null(patch.TemplateType);
        Assert.Equal("some_template", patch.TemplateId);
    }

    [Fact]
    public void Manifest_FromJson_IndexedFieldPath_Deserialises()
    {
        var json = """
        {
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

        var manifest = CompiledTemplatePatchManifest.FromJson(json);
        var op = Assert.Single(manifest.TemplatePatches![0].Set);
        Assert.Equal("Properties.MoraleEvents[2].SomeField", op.FieldPath);
        Assert.Equal(42, op.Value!.Int32);
    }

    [Fact]
    public void TryLoad_RoundTripsTemplatesJsonFromDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"jiangyu-templates-load-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var original = new CompiledTemplatePatchManifest
            {
                TemplatePatches =
                [
                    new CompiledTemplatePatch { TemplateType = "PerkTemplate", TemplateId = "perk.x", Set = [] },
                ],
                TemplateClones =
                [
                    new CompiledTemplateClone { TemplateType = "PerkTemplate", CloneId = "perk.y" },
                ],
            };
            File.WriteAllText(Path.Combine(dir, CompiledTemplatePatchManifest.FileName), original.ToJson());

            var loaded = CompiledTemplatePatchManifest.TryLoad(dir);

            Assert.NotNull(loaded);
            Assert.Equal("perk.x", Assert.Single(loaded!.TemplatePatches!).TemplateId);
            Assert.Equal("perk.y", Assert.Single(loaded.TemplateClones!).CloneId);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TryLoad_ReturnsNullWhenNoTemplatesJsonPresent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"jiangyu-templates-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try { Assert.Null(CompiledTemplatePatchManifest.TryLoad(dir)); }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
