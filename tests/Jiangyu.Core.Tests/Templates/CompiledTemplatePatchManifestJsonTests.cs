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
                new TemplateDescentStep { Field = "EventHandlers", Index = 0, Subtype = "AddSkill" },
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
        Assert.Contains("\"subtype\":\"AddSkill\"", json);
        Assert.Contains("\"AddSkill\"", json);

        var roundTripped = JsonSerializer.Deserialize<CompiledTemplateSetOperation>(json, Options);
        Assert.NotNull(roundTripped);
        Assert.Equal("ShowHUDText", roundTripped!.FieldPath);
        Assert.NotNull(roundTripped.Descent);
        Assert.Single(roundTripped.Descent);
        Assert.Equal("EventHandlers", roundTripped.Descent[0].Field);
        Assert.Equal(0, roundTripped.Descent[0].Index);
        Assert.Equal("AddSkill", roundTripped.Descent[0].Subtype);
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
                new TemplateDescentStep { Field = "Outer", Index = 0, Subtype = "TypeX" },
                new TemplateDescentStep { Field = "Inner", Index = 2, Subtype = "TypeY" },
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
        Assert.Equal("TypeX", roundTripped.Descent[0].Subtype);
        Assert.Equal("Inner", roundTripped.Descent[1].Field);
        Assert.Equal(2, roundTripped.Descent[1].Index);
        Assert.Equal("TypeY", roundTripped.Descent[1].Subtype);
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
    public void HandlerConstruction_RoundTrip()
    {
        var op = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Append,
            FieldPath = "EventHandlers",
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.HandlerConstruction,
                HandlerConstruction = new CompiledTemplateComposite
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
        Assert.Contains("\"HandlerConstruction\"", json);
        Assert.Contains("\"handlerConstruction\"", json);
        Assert.Contains("\"AddSkill\"", json);
        Assert.Contains("\"OnAttack\"", json);

        var roundTripped = JsonSerializer.Deserialize<CompiledTemplateSetOperation>(json, Options);
        Assert.NotNull(roundTripped);
        Assert.Equal(CompiledTemplateValueKind.HandlerConstruction, roundTripped!.Value!.Kind);
        Assert.NotNull(roundTripped.Value.HandlerConstruction);
        Assert.Equal("AddSkill", roundTripped.Value.HandlerConstruction!.TypeName);
        Assert.Equal(2, roundTripped.Value.HandlerConstruction.Operations.Count);
        Assert.Equal("OnAttack", roundTripped.Value.HandlerConstruction.ValueAt("Event").EnumValue);
        Assert.True(roundTripped.Value.HandlerConstruction.ValueAt("ShowHUDText").Boolean);
    }

    [Fact]
    public void HandlerConstruction_AndDescent_CoexistInOneOp()
    {
        // A "replace element N with new construction" op can carry both
        // Descent (in case the path descends through a polymorphic
        // intermediate; rare) and a HandlerConstruction value at the
        // terminal. Both must round-trip together.
        var op = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Set,
            FieldPath = "EventHandlers",
            Index = 1,
            Descent =
            [
                new TemplateDescentStep { Field = "Outer", Index = 0, Subtype = "AddSkill" },
            ],
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.HandlerConstruction,
                HandlerConstruction = new CompiledTemplateComposite
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
        Assert.Equal("AddSkill", roundTripped.Descent[0].Subtype);
        Assert.Equal("ChangeProperty", roundTripped.Value!.HandlerConstruction!.TypeName);
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
    public void CompositeAndHandlerConstruction_AreDistinctValueKinds()
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
                Kind = CompiledTemplateValueKind.HandlerConstruction,
                HandlerConstruction = new CompiledTemplateComposite { TypeName = "AddSkill", Operations = SetOps(("ShowHUDText", new() { Kind = CompiledTemplateValueKind.Boolean, Boolean = true })) },
            },
        };

        var compositeJson = JsonSerializer.Serialize(compositeOp, Options);
        var handlerJson = JsonSerializer.Serialize(handlerOp, Options);

        Assert.Contains("\"Composite\"", compositeJson);
        Assert.DoesNotContain("\"HandlerConstruction\"", compositeJson);
        Assert.Contains("\"HandlerConstruction\"", handlerJson);
        Assert.DoesNotContain("\"Composite\"", handlerJson);
    }
}
