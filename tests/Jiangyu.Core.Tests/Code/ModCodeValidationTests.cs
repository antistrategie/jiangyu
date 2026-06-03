using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Code;
using Jiangyu.Shared.Templates;
using Xunit;

namespace Jiangyu.Core.Tests.Code;

public sealed class ModCodeValidationTests
{
    private static CompiledTemplatePatch PatchWithTypeConstruction(string typeName, params CompiledTemplateSetOperation[] innerOps)
        => new()
        {
            TemplateType = "PerkTemplate",
            TemplateId = "perk.x",
            Set =
            [
                new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Append,
                    FieldPath = "EventHandlers",
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.TypeConstruction,
                        TypeConstruction = new CompiledTemplateComposite
                        {
                            TypeName = typeName,
                            Operations = innerOps.ToList(),
                        },
                    },
                },
            ],
        };

    [Fact]
    public void KnownModType_NoErrors()
    {
        var patches = new[] { PatchWithTypeConstruction("WOMENACE:LastAegis") };
        var known = new HashSet<string>(StringComparer.Ordinal) { "LastAegis" };

        Assert.Equal(0, ModCodeValidation.Validate(patches, known, new NullLog()));
    }

    [Fact]
    public void UnknownModType_IsReported()
    {
        var patches = new[] { PatchWithTypeConstruction("WOMENACE:Typod") };
        var known = new HashSet<string>(StringComparer.Ordinal) { "LastAegis" };

        Assert.Equal(1, ModCodeValidation.Validate(patches, known, new NullLog()));
    }

    [Fact]
    public void GameType_WithoutColon_IsIgnored()
    {
        // Dotted Il2Cpp game types carry no colon, so they are not mod-type
        // references and must not be cross-checked against the code DLL.
        var patches = new[] { PatchWithTypeConstruction("Il2CppMenace.Tactical.Skills.Effects.ApplySkillToSelf") };
        var known = new HashSet<string>(StringComparer.Ordinal);

        Assert.Equal(0, ModCodeValidation.Validate(patches, known, new NullLog()));
    }

    [Fact]
    public void NestedComposite_IsChecked()
    {
        var innerOp = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Set,
            FieldPath = "StatusEffect",
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.TypeConstruction,
                TypeConstruction = new CompiledTemplateComposite { TypeName = "WOMENACE:Inner" },
            },
        };
        var patches = new[] { PatchWithTypeConstruction("WOMENACE:Outer", innerOp) };
        var known = new HashSet<string>(StringComparer.Ordinal) { "Outer" }; // Inner missing

        Assert.Equal(1, ModCodeValidation.Validate(patches, known, new NullLog()));
    }

    [Fact]
    public void ModTypeRef_WithNoCodeDll_IsReported()
    {
        // A type="ns:Name" with no built code DLL (empty known set) is broken:
        // it can never resolve at load time.
        var patches = new[] { PatchWithTypeConstruction("WOMENACE:LastAegis") };
        var known = new HashSet<string>(StringComparer.Ordinal);

        Assert.Equal(1, ModCodeValidation.Validate(patches, known, new NullLog()));
    }

    [Fact]
    public void NullPatches_NoErrors()
    {
        Assert.Equal(0, ModCodeValidation.Validate(null, new HashSet<string>(), new NullLog()));
    }

    [Fact]
    public void NearMissModType_SuggestsClosestName()
    {
        var patches = new[] { PatchWithTypeConstruction("WOMENACE:DeathDefyign") };
        var known = new HashSet<string>(StringComparer.Ordinal) { "DeathDefying", "ApplyAura" };
        var log = new CollectingLog();

        Assert.Equal(1, ModCodeValidation.Validate(patches, known, log));
        Assert.Contains(log.Errors, e => e.Contains("Did you mean 'DeathDefying'?"));
    }

    [Fact]
    public void WildlyDifferentModType_OffersNoSuggestion()
    {
        var patches = new[] { PatchWithTypeConstruction("WOMENACE:Zzz") };
        var known = new HashSet<string>(StringComparer.Ordinal) { "DeathDefying" };
        var log = new CollectingLog();

        Assert.Equal(1, ModCodeValidation.Validate(patches, known, log));
        Assert.DoesNotContain(log.Errors, e => e.Contains("Did you mean"));
    }

    private sealed class NullLog : ILogSink
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
    }

    private sealed class CollectingLog : ILogSink
    {
        public readonly List<string> Errors = new();
        public readonly List<string> Warnings = new();
        public void Info(string message) { }
        public void Warning(string message) => Warnings.Add(message);
        public void Error(string message) => Errors.Add(message);
    }
}
