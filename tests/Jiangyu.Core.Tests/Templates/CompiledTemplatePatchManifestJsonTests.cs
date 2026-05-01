using System.Text.Json;
using Jiangyu.Shared.Templates;

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
    public void SubtypeHints_RoundTrip()
    {
        var op = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Set,
            FieldPath = "EventHandlers[0].ShowHUDText",
            SubtypeHints = new Dictionary<int, string>
            {
                [0] = "AddSkill",
            },
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Boolean,
                Boolean = true,
            },
        };

        var json = JsonSerializer.Serialize(op, Options);
        Assert.Contains("\"subtypeHints\"", json);
        Assert.Contains("\"AddSkill\"", json);

        var roundTripped = JsonSerializer.Deserialize<CompiledTemplateSetOperation>(json, Options);
        Assert.NotNull(roundTripped);
        Assert.Equal("EventHandlers[0].ShowHUDText", roundTripped!.FieldPath);
        Assert.NotNull(roundTripped.SubtypeHints);
        Assert.Equal("AddSkill", roundTripped.SubtypeHints![0]);
    }

    [Fact]
    public void SubtypeHints_MultipleSegments_RoundTrip()
    {
        // Multi-level descent: hints at distinct segment indices need to
        // survive the JSON round-trip with both keys intact.
        var op = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Set,
            FieldPath = "Outer[0].Inner[2].Leaf",
            SubtypeHints = new Dictionary<int, string>
            {
                [0] = "TypeX",
                [1] = "TypeY",
            },
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Int32,
                Int32 = 5,
            },
        };

        var json = JsonSerializer.Serialize(op, Options);
        var roundTripped = JsonSerializer.Deserialize<CompiledTemplateSetOperation>(json, Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(2, roundTripped!.SubtypeHints!.Count);
        Assert.Equal("TypeX", roundTripped.SubtypeHints[0]);
        Assert.Equal("TypeY", roundTripped.SubtypeHints[1]);
    }

    [Fact]
    public void NullSubtypeHints_OmittedFromJson()
    {
        // The wire format optimises for the common case (no descent through
        // a polymorphic boundary) by omitting null SubtypeHints entirely
        // from the serialised output. Keeps compiled bundles small.
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

        Assert.DoesNotContain("subtypeHints", json);

        var roundTripped = JsonSerializer.Deserialize<CompiledTemplateSetOperation>(json, Options);
        Assert.Null(roundTripped!.SubtypeHints);
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
                    Fields = new Dictionary<string, CompiledTemplateValue>
                    {
                        ["Event"] = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.Enum,
                            EnumType = "AddEvent",
                            EnumValue = "OnAttack",
                        },
                        ["ShowHUDText"] = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.Boolean,
                            Boolean = true,
                        },
                    },
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
        Assert.Equal(2, roundTripped.Value.HandlerConstruction.Fields.Count);
        Assert.Equal("OnAttack", roundTripped.Value.HandlerConstruction.Fields["Event"].EnumValue);
        Assert.True(roundTripped.Value.HandlerConstruction.Fields["ShowHUDText"].Boolean);
    }

    [Fact]
    public void HandlerConstruction_AndSubtypeHints_CoexistInOneOp()
    {
        // A "replace element N with new construction" op can carry both
        // SubtypeHints (in case the path descends through a polymorphic
        // intermediate; rare) and a HandlerConstruction value at the
        // terminal. Both must round-trip together.
        var op = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Set,
            FieldPath = "EventHandlers",
            Index = 1,
            SubtypeHints = new Dictionary<int, string> { [0] = "AddSkill" },
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.HandlerConstruction,
                HandlerConstruction = new CompiledTemplateComposite
                {
                    TypeName = "ChangeProperty",
                    Fields = new Dictionary<string, CompiledTemplateValue>
                    {
                        ["Amount"] = new() { Kind = CompiledTemplateValueKind.Int32, Int32 = 5 },
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(op, Options);
        var roundTripped = JsonSerializer.Deserialize<CompiledTemplateSetOperation>(json, Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(1, roundTripped!.Index);
        Assert.Equal("AddSkill", roundTripped.SubtypeHints![0]);
        Assert.Equal("ChangeProperty", roundTripped.Value!.HandlerConstruction!.TypeName);
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
                Composite = new CompiledTemplateComposite { TypeName = "Perk", Fields = new() { ["Tier"] = new() { Kind = CompiledTemplateValueKind.Int32, Int32 = 1 } } },
            },
        };
        var handlerOp = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Append,
            FieldPath = "EventHandlers",
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.HandlerConstruction,
                HandlerConstruction = new CompiledTemplateComposite { TypeName = "AddSkill", Fields = new() { ["ShowHUDText"] = new() { Kind = CompiledTemplateValueKind.Boolean, Boolean = true } } },
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
