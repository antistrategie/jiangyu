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
        Assert.Contains("incomplete value", log.Errors[0]);
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
