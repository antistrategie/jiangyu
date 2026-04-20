using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Compile;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Tests.Compile;

public class TemplatePatchEmitterTests
{
    [Fact]
    public void NullOrEmptyInput_ReturnsSuccessWithNoRewrites()
    {
        var log = new RecordingLogSink();

        var resultForNull = TemplatePatchEmitter.Emit(null, log);
        var resultForEmpty = TemplatePatchEmitter.Emit([], log);

        Assert.True(resultForNull.Success);
        Assert.Equal(0, resultForNull.RewriteCount);
        Assert.True(resultForEmpty.Success);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void SugarPath_IsRewrittenToCanonicalIndexedForm()
    {
        var log = new RecordingLogSink();
        var patches = new List<CompiledTemplatePatch>
        {
            BuildPatch("UnitLeaderTemplate", "hero.elena", "InitialAttributes.Vitality", Byte(12)),
        };

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.True(result.Success);
        Assert.Equal(1, result.RewriteCount);
        var emitted = Assert.Single(result.Patches!);
        var op = Assert.Single(emitted.Set);
        Assert.Equal("InitialAttributes[4]", op.FieldPath);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void AlreadyIndexedPath_PassesThroughWithoutRewrite()
    {
        var log = new RecordingLogSink();
        var patches = new List<CompiledTemplatePatch>
        {
            BuildPatch("UnitLeaderTemplate", "hero.elena", "InitialAttributes[2]", Byte(8)),
        };

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.True(result.Success);
        Assert.Equal(0, result.RewriteCount);
        var op = Assert.Single(result.Patches!.Single().Set);
        Assert.Equal("InitialAttributes[2]", op.FieldPath);
    }

    [Fact]
    public void EmittingTwice_IsIdempotent()
    {
        var log = new RecordingLogSink();
        var patches = new List<CompiledTemplatePatch>
        {
            BuildPatch("UnitLeaderTemplate", "hero.elena", "InitialAttributes.Precision", Byte(5)),
        };

        var first = TemplatePatchEmitter.Emit(patches, log);
        Assert.Equal(1, first.RewriteCount);

        var second = TemplatePatchEmitter.Emit(first.Patches, log);

        Assert.True(second.Success);
        Assert.Equal(0, second.RewriteCount);
        Assert.Equal("InitialAttributes[5]", second.Patches!.Single().Set.Single().FieldPath);
    }

    [Fact]
    public void UnknownSugarAttribute_EscalatesToCompileError()
    {
        var log = new RecordingLogSink();
        var patches = new List<CompiledTemplatePatch>
        {
            BuildPatch("UnitLeaderTemplate", "hero.elena", "InitialAttributes.NopeNotAThing", Byte(1)),
        };

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.False(result.Success);
        Assert.Equal(1, result.ErrorCount);
        Assert.Single(log.Errors);
        Assert.Contains("NopeNotAThing", log.Errors[0]);
    }

    [Fact]
    public void UnsupportedPathSyntax_EscalatesToCompileError()
    {
        var log = new RecordingLogSink();
        var patches = new List<CompiledTemplatePatch>
        {
            BuildPatch("EntityTemplate", "unit.marine", "Skills(0)", Int32(10)),
        };

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.False(result.Success);
        Assert.Contains("unsupported path syntax", log.Errors[0]);
    }

    [Fact]
    public void IncompleteValue_EscalatesToCompileError()
    {
        var log = new RecordingLogSink();
        var patches = new List<CompiledTemplatePatch>
        {
            BuildPatch("EntityTemplate", "unit.marine", "MaxHealth", new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Int32,
                // Int32 left null — value is declared Int32 but carries no payload.
            }),
        };

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.False(result.Success);
        Assert.Contains("unsupported or incomplete", log.Errors[0]);
    }

    [Fact]
    public void EmptyTemplateId_EscalatesToCompileError()
    {
        var log = new RecordingLogSink();
        var patches = new List<CompiledTemplatePatch>
        {
            new()
            {
                TemplateType = "EntityTemplate",
                TemplateId = "",
                Set = [new() { FieldPath = "MaxHealth", Value = Int32(10) }],
            },
        };

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.False(result.Success);
        Assert.Contains("templateId is empty", log.Errors[0]);
    }

    [Fact]
    public void MissingSet_EscalatesToCompileError()
    {
        var log = new RecordingLogSink();
        var patches = new List<CompiledTemplatePatch>
        {
            new()
            {
                TemplateType = "EntityTemplate",
                TemplateId = "unit.marine",
                Set = [],
            },
        };

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.False(result.Success);
        Assert.Contains("no 'set' operations", log.Errors[0]);
    }

    [Fact]
    public void NonSugarTemplateType_LeavesPathUntouched()
    {
        var log = new RecordingLogSink();
        var patches = new List<CompiledTemplatePatch>
        {
            BuildPatch("EntityTemplate", "unit.marine", "InitialAttributes.Agility", Byte(7)),
        };

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.True(result.Success);
        Assert.Equal(0, result.RewriteCount);
        Assert.Equal("InitialAttributes.Agility", result.Patches!.Single().Set.Single().FieldPath);
    }

    [Fact]
    public void MixedValidAndInvalid_AggregatesAllErrors()
    {
        var log = new RecordingLogSink();
        var patches = new List<CompiledTemplatePatch>
        {
            BuildPatch("UnitLeaderTemplate", "hero.elena", "InitialAttributes.Bogus", Byte(1)),
            BuildPatch("UnitLeaderTemplate", "hero.zara", "InitialAttributes.Agility", Byte(9)),
            BuildPatch("EntityTemplate", "unit.marine", "Skills(0)", Int32(1)),
        };

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.False(result.Success);
        Assert.Equal(2, result.ErrorCount);
        Assert.Equal(2, log.Errors.Count);
    }

    [Fact]
    public void EmitClones_NullOrEmpty_ReturnsSuccess()
    {
        var log = new RecordingLogSink();

        var forNull = TemplatePatchEmitter.EmitClones(null, log);
        var forEmpty = TemplatePatchEmitter.EmitClones([], log);

        Assert.True(forNull.Success);
        Assert.True(forEmpty.Success);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void EmitClones_HappyPath_TrimsTemplateType()
    {
        var log = new RecordingLogSink();
        var clones = new List<CompiledTemplateClone>
        {
            new() { TemplateType = "  EntityTemplate  ", SourceId = "a", CloneId = "b" },
        };

        var result = TemplatePatchEmitter.EmitClones(clones, log);

        Assert.True(result.Success);
        var emitted = Assert.Single(result.Clones!);
        Assert.Equal("EntityTemplate", emitted.TemplateType);
        Assert.Equal("a", emitted.SourceId);
        Assert.Equal("b", emitted.CloneId);
    }

    [Fact]
    public void EmitClones_MissingTemplateType_Errors()
    {
        var log = new RecordingLogSink();
        var clones = new List<CompiledTemplateClone>
        {
            new() { TemplateType = null, SourceId = "a", CloneId = "b" },
        };

        var result = TemplatePatchEmitter.EmitClones(clones, log);

        Assert.False(result.Success);
        Assert.Contains("templateType is required", log.Errors[0]);
    }

    [Fact]
    public void EmitClones_EmptySourceOrCloneId_Errors()
    {
        var log = new RecordingLogSink();
        var clones = new List<CompiledTemplateClone>
        {
            new() { TemplateType = "EntityTemplate", SourceId = "", CloneId = "b" },
            new() { TemplateType = "EntityTemplate", SourceId = "a", CloneId = "" },
        };

        var result = TemplatePatchEmitter.EmitClones(clones, log);

        Assert.Equal(2, result.ErrorCount);
        Assert.Contains("sourceId is empty", log.Errors[0]);
        Assert.Contains("cloneId is empty", log.Errors[1]);
    }

    [Fact]
    public void EmitClones_SourceEqualsClone_Errors()
    {
        var log = new RecordingLogSink();
        var clones = new List<CompiledTemplateClone>
        {
            new() { TemplateType = "EntityTemplate", SourceId = "a", CloneId = "a" },
        };

        var result = TemplatePatchEmitter.EmitClones(clones, log);

        Assert.False(result.Success);
        Assert.Contains("must differ from sourceId", log.Errors[0]);
    }

    [Fact]
    public void EmitClones_DuplicateCloneIdWithinBatch_Errors()
    {
        var log = new RecordingLogSink();
        var clones = new List<CompiledTemplateClone>
        {
            new() { TemplateType = "EntityTemplate", SourceId = "a", CloneId = "c" },
            new() { TemplateType = "EntityTemplate", SourceId = "b", CloneId = "c" },
        };

        var result = TemplatePatchEmitter.EmitClones(clones, log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains("duplicate cloneId", log.Errors[0]);
    }

    [Fact]
    public void EmitClones_SameCloneIdDifferentTemplateType_IsAllowed()
    {
        var log = new RecordingLogSink();
        var clones = new List<CompiledTemplateClone>
        {
            new() { TemplateType = "EntityTemplate", SourceId = "a", CloneId = "c" },
            new() { TemplateType = "UnitLeaderTemplate", SourceId = "b", CloneId = "c" },
        };

        var result = TemplatePatchEmitter.EmitClones(clones, log);

        Assert.True(result.Success);
        Assert.Equal(2, result.Clones!.Count);
    }

    [Fact]
    public void AppendOp_OnSimpleFieldPath_PassesThrough()
    {
        var log = new RecordingLogSink();
        var patches = new List<CompiledTemplatePatch>
        {
            new()
            {
                TemplateType = "EntityTemplate",
                TemplateId = "unit.marine",
                Set = [new() { Op = CompiledTemplateOp.Append, FieldPath = "Skills", Value = Int32(10) }],
            },
        };

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.True(result.Success);
        var emittedOp = result.Patches!.Single().Set.Single();
        Assert.Equal(CompiledTemplateOp.Append, emittedOp.Op);
        Assert.Equal("Skills", emittedOp.FieldPath);
    }

    [Fact]
    public void AppendOp_WithIndexedTerminal_Errors()
    {
        var log = new RecordingLogSink();
        var patches = new List<CompiledTemplatePatch>
        {
            new()
            {
                TemplateType = "EntityTemplate",
                TemplateId = "unit.marine",
                Set = [new() { Op = CompiledTemplateOp.Append, FieldPath = "Skills[3]", Value = Int32(10) }],
            },
        };

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.False(result.Success);
        Assert.Contains("op=Append", log.Errors[0]);
        Assert.Contains("indexed terminal", log.Errors[0]);
    }

    [Fact]
    public void AppendOp_WithNestedNonTerminalIndex_IsAllowed()
    {
        var log = new RecordingLogSink();
        var patches = new List<CompiledTemplatePatch>
        {
            new()
            {
                TemplateType = "EntityTemplate",
                TemplateId = "unit.marine",
                Set = [new() { Op = CompiledTemplateOp.Append, FieldPath = "Groups[0].Skills", Value = Int32(10) }],
            },
        };

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.True(result.Success);
        Assert.Equal("Groups[0].Skills", result.Patches!.Single().Set.Single().FieldPath);
    }

    [Fact]
    public void SetOp_WithIndexField_Errors()
    {
        var log = new RecordingLogSink();
        var patches = SinglePatch("EntityTemplate", "unit.marine",
            new CompiledTemplateSetOperation { Op = CompiledTemplateOp.Set, FieldPath = "Skills", Index = 1, Value = Int32(10) });

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.False(result.Success);
        Assert.Contains("op=Set cannot carry an 'index'", log.Errors[0]);
    }

    [Fact]
    public void InsertAtOp_HappyPath_PassesThrough()
    {
        var log = new RecordingLogSink();
        var patches = SinglePatch("EntityTemplate", "unit.marine",
            new CompiledTemplateSetOperation { Op = CompiledTemplateOp.InsertAt, FieldPath = "Skills", Index = 2, Value = Int32(10) });

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.True(result.Success);
        var op = result.Patches!.Single().Set.Single();
        Assert.Equal(CompiledTemplateOp.InsertAt, op.Op);
        Assert.Equal(2, op.Index);
    }

    [Fact]
    public void InsertAtOp_MissingIndex_Errors()
    {
        var log = new RecordingLogSink();
        var patches = SinglePatch("EntityTemplate", "unit.marine",
            new CompiledTemplateSetOperation { Op = CompiledTemplateOp.InsertAt, FieldPath = "Skills", Value = Int32(10) });

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.False(result.Success);
        Assert.Contains("op=InsertAt requires an 'index'", log.Errors[0]);
    }

    [Fact]
    public void InsertAtOp_NegativeIndex_Errors()
    {
        var log = new RecordingLogSink();
        var patches = SinglePatch("EntityTemplate", "unit.marine",
            new CompiledTemplateSetOperation { Op = CompiledTemplateOp.InsertAt, FieldPath = "Skills", Index = -1, Value = Int32(10) });

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.False(result.Success);
        Assert.Contains("negative index", log.Errors[0]);
    }

    [Fact]
    public void InsertAtOp_IndexedTerminal_Errors()
    {
        var log = new RecordingLogSink();
        var patches = SinglePatch("EntityTemplate", "unit.marine",
            new CompiledTemplateSetOperation { Op = CompiledTemplateOp.InsertAt, FieldPath = "Skills[0]", Index = 1, Value = Int32(10) });

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.False(result.Success);
        Assert.Contains("op=InsertAt cannot have an indexed terminal", log.Errors[0]);
    }

    [Fact]
    public void RemoveOp_HappyPath_PassesThrough()
    {
        var log = new RecordingLogSink();
        var patches = SinglePatch("EntityTemplate", "unit.marine",
            new CompiledTemplateSetOperation { Op = CompiledTemplateOp.Remove, FieldPath = "Skills[2]" });

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.True(result.Success);
        var op = result.Patches!.Single().Set.Single();
        Assert.Equal(CompiledTemplateOp.Remove, op.Op);
        Assert.Equal("Skills[2]", op.FieldPath);
    }

    [Fact]
    public void RemoveOp_NonIndexedTerminal_Errors()
    {
        var log = new RecordingLogSink();
        var patches = SinglePatch("EntityTemplate", "unit.marine",
            new CompiledTemplateSetOperation { Op = CompiledTemplateOp.Remove, FieldPath = "Skills" });

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.False(result.Success);
        Assert.Contains("op=Remove requires an indexed terminal", log.Errors[0]);
    }

    [Fact]
    public void RemoveOp_WithValue_Errors()
    {
        var log = new RecordingLogSink();
        var patches = SinglePatch("EntityTemplate", "unit.marine",
            new CompiledTemplateSetOperation { Op = CompiledTemplateOp.Remove, FieldPath = "Skills[2]", Value = Int32(10) });

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.False(result.Success);
        Assert.Contains("op=Remove cannot carry a value", log.Errors[0]);
    }

    [Fact]
    public void RemoveOp_WithIndexField_Errors()
    {
        var log = new RecordingLogSink();
        var patches = SinglePatch("EntityTemplate", "unit.marine",
            new CompiledTemplateSetOperation { Op = CompiledTemplateOp.Remove, FieldPath = "Skills[2]", Index = 2 });

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.False(result.Success);
        Assert.Contains("op=Remove cannot carry an 'index'", log.Errors[0]);
    }

    [Fact]
    public void CompositeValue_Minimal_PassesThrough()
    {
        var log = new RecordingLogSink();
        var patches = SinglePatch("UnitLeaderTemplate", "hero.elena",
            new CompiledTemplateSetOperation
            {
                Op = CompiledTemplateOp.Append,
                FieldPath = "PerkTrees[0].Perks",
                Value = new CompiledTemplateValue
                {
                    Kind = CompiledTemplateValueKind.Composite,
                    Composite = new CompiledTemplateComposite
                    {
                        TypeName = "Perk",
                        Fields = new Dictionary<string, CompiledTemplateValue>
                        {
                            ["Tier"] = Int32(3),
                            ["Skill"] = new CompiledTemplateValue
                            {
                                Kind = CompiledTemplateValueKind.TemplateReference,
                                Reference = new CompiledTemplateReference
                                {
                                    TemplateType = "PerkTemplate",
                                    TemplateId = "perk.athletic",
                                },
                            },
                        },
                    },
                },
            });

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.True(result.Success);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void CompositeValue_MissingTypeName_Errors()
    {
        var log = new RecordingLogSink();
        var patches = SinglePatch("UnitLeaderTemplate", "hero.elena",
            new CompiledTemplateSetOperation
            {
                Op = CompiledTemplateOp.Append,
                FieldPath = "PerkTrees[0].Perks",
                Value = new CompiledTemplateValue
                {
                    Kind = CompiledTemplateValueKind.Composite,
                    Composite = new CompiledTemplateComposite
                    {
                        TypeName = "",
                        Fields = new Dictionary<string, CompiledTemplateValue> { ["Tier"] = Int32(3) },
                    },
                },
            });

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.False(result.Success);
        Assert.Contains("unsupported or incomplete", log.Errors[0]);
    }

    [Fact]
    public void CompositeValue_EmptyFields_Errors()
    {
        var log = new RecordingLogSink();
        var patches = SinglePatch("UnitLeaderTemplate", "hero.elena",
            new CompiledTemplateSetOperation
            {
                Op = CompiledTemplateOp.Append,
                FieldPath = "PerkTrees[0].Perks",
                Value = new CompiledTemplateValue
                {
                    Kind = CompiledTemplateValueKind.Composite,
                    Composite = new CompiledTemplateComposite { TypeName = "Perk", Fields = new() },
                },
            });

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.False(result.Success);
    }

    [Fact]
    public void CompositeValue_NestedComposite_PassesThrough()
    {
        var log = new RecordingLogSink();
        var inner = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Composite,
            Composite = new CompiledTemplateComposite
            {
                TypeName = "InnerType",
                Fields = new() { ["Leaf"] = Int32(1) },
            },
        };
        var patches = SinglePatch("EntityTemplate", "unit.marine",
            new CompiledTemplateSetOperation
            {
                Op = CompiledTemplateOp.Set,
                FieldPath = "Nested",
                Value = new CompiledTemplateValue
                {
                    Kind = CompiledTemplateValueKind.Composite,
                    Composite = new CompiledTemplateComposite
                    {
                        TypeName = "OuterType",
                        Fields = new() { ["Child"] = inner },
                    },
                },
            });

        var result = TemplatePatchEmitter.Emit(patches, log);

        Assert.True(result.Success);
    }

    private static List<CompiledTemplatePatch> SinglePatch(string templateType, string templateId, CompiledTemplateSetOperation op)
        => new()
        {
            new CompiledTemplatePatch
            {
                TemplateType = templateType,
                TemplateId = templateId,
                Set = [op],
            },
        };

    private static CompiledTemplatePatch BuildPatch(string templateType, string templateId, string fieldPath, CompiledTemplateValue value)
        => new()
        {
            TemplateType = templateType,
            TemplateId = templateId,
            Set = [new() { FieldPath = fieldPath, Value = value }],
        };

    private static CompiledTemplateValue Byte(byte value)
        => new() { Kind = CompiledTemplateValueKind.Byte, Byte = value };

    private static CompiledTemplateValue Int32(int value)
        => new() { Kind = CompiledTemplateValueKind.Int32, Int32 = value };

    private sealed class RecordingLogSink : ILogSink
    {
        public List<string> Errors { get; } = [];
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) => Errors.Add(message);
    }
}
