using Jiangyu.Core.Templates;
using Jiangyu.Shared.Templates;
using static Jiangyu.Core.Tests.Templates.CompiledTemplateTestHelpers;

namespace Jiangyu.Core.Tests.Templates;

public class CompositeAutoFillersTests
{
    // --- Pre-validation: clone-identity injection ---

    [Fact]
    public void PreValidation_SoundBank_InjectsBankIdFromCloneId()
    {
        var patch = new CompiledTemplatePatch
        {
            TemplateType = "SoundBank",
            TemplateId = "tactical_barks_voymastina_va",
            Set = [SetOp("someUnrelatedField", StringValue("x"))],
        };

        CompositeAutoFillers.ApplyPreValidation([patch]);

        var bankIdOp = patch.Set.First(o => o.FieldPath == "bankId");
        Assert.Equal(CompiledTemplateOp.Set, bankIdOp.Op);
        Assert.Equal(CompiledTemplateValueKind.String, bankIdOp.Value!.Kind);
        Assert.Equal("tactical_barks_voymastina_va", bankIdOp.Value.String);
    }

    [Fact]
    public void PreValidation_ConversationTemplate_InjectsPathFromCloneId()
    {
        var patch = new CompiledTemplatePatch
        {
            TemplateType = "ConversationTemplate",
            TemplateId = "Voymastina/click_bark",
            Set = [],
        };

        CompositeAutoFillers.ApplyPreValidation([patch]);

        var pathOp = patch.Set.First(o => o.FieldPath == "Path");
        Assert.Equal(CompiledTemplateValueKind.String, pathOp.Value!.Kind);
        Assert.Equal("Voymastina/click_bark", pathOp.Value.String);
    }

    [Fact]
    public void PreValidation_SkipsWhenModderSetIdentityExplicitly()
    {
        // Modder overrode bankId explicitly. Filler must not append a second.
        var patch = new CompiledTemplatePatch
        {
            TemplateType = "SoundBank",
            TemplateId = "ignored_clone_id",
            Set = [SetOp("bankId", StringValue("explicit_value"))],
        };

        CompositeAutoFillers.ApplyPreValidation([patch]);

        var bankIdOps = patch.Set.Where(o => o.FieldPath == "bankId").ToList();
        Assert.Single(bankIdOps);
        Assert.Equal("explicit_value", bankIdOps[0].Value!.String);
    }

    [Fact]
    public void PreValidation_NonSoundBankNonConversation_IsNoOp()
    {
        var patch = new CompiledTemplatePatch
        {
            TemplateType = "UnitLeaderTemplate",
            TemplateId = "hero.elena",
            Set = [SetOp("Name", StringValue("Elena"))],
        };

        CompositeAutoFillers.ApplyPreValidation([patch]);

        Assert.Single(patch.Set);
    }

    [Fact]
    public void PreValidation_Idempotent()
    {
        var patch = new CompiledTemplatePatch
        {
            TemplateType = "SoundBank",
            TemplateId = "test_bank",
            Set = [],
        };

        CompositeAutoFillers.ApplyPreValidation([patch]);
        CompositeAutoFillers.ApplyPreValidation([patch]);
        CompositeAutoFillers.ApplyPreValidation([patch]);

        var bankIdOps = patch.Set.Where(o => o.FieldPath == "bankId").ToList();
        Assert.Single(bankIdOps);
    }

    // --- Post-validation: Sound.id from name ---

    [Theory]
    [InlineData("Sound")]
    [InlineData("Stem.Sound")]
    [InlineData("Il2CppStem.Sound")]
    public void PostValidation_SoundComposite_FillsIdFromName(string typeName)
    {
        var sound = new CompiledTemplateComposite
        {
            TypeName = typeName,
            Operations = [SetOp("name", StringValue("voymastina_click_bark_test"))],
        };
        var patch = WrapInPatch("Whatever", "x", "Sounds", sound);

        CompositeAutoFillers.ApplyPostValidation([patch]);

        var idOp = sound.Operations.First(o => o.FieldPath == "id");
        Assert.Equal(CompiledTemplateValueKind.Int32, idOp.Value!.Kind);
        Assert.Equal(HashableIdFieldRegistry.Fnv1a32("voymastina_click_bark_test"), idOp.Value.Int32);
    }

    [Fact]
    public void PostValidation_SoundComposite_SkipsWhenIdExplicit()
    {
        var sound = new CompiledTemplateComposite
        {
            TypeName = "Sound",
            Operations =
            [
                SetOp("name", StringValue("a")),
                SetOp("id", Int32Value(42)),
            ],
        };
        var patch = WrapInPatch("Whatever", "x", "Sounds", sound);

        CompositeAutoFillers.ApplyPostValidation([patch]);

        var idOps = sound.Operations.Where(o => o.FieldPath == "id").ToList();
        Assert.Single(idOps);
        Assert.Equal(42, idOps[0].Value!.Int32);
    }

    [Fact]
    public void PostValidation_SoundComposite_NoNameLeavesIdAlone()
    {
        // No name => no value to hash. Caller will fail catalog validation
        // elsewhere; filler stays out of the way.
        var sound = new CompiledTemplateComposite
        {
            TypeName = "Sound",
            Operations = [SetOp("fixedVolume", Int32Value(1))],
        };
        var patch = WrapInPatch("Whatever", "x", "Sounds", sound);

        CompositeAutoFillers.ApplyPostValidation([patch]);

        Assert.DoesNotContain(sound.Operations, o => o.FieldPath == "id");
    }

    // --- Post-validation: VariationCopyCount sync ---

    [Fact]
    public void PostValidation_VariationCopyCount_PadsToMatchVariations()
    {
        // Three `append "Variations"` ops, no copy-count ops authored.
        // Filler should add three `append "VariationCopyCount"` with int 1.
        var variation = new CompiledTemplateComposite
        {
            TypeName = "Il2CppMenace.Conversations.VariationConversationNode",
            Operations =
            [
                AppendOp("Variations", CompositeValue("Il2CppMenace.Conversations.ConversationNodeContainer", [])),
                AppendOp("Variations", CompositeValue("Il2CppMenace.Conversations.ConversationNodeContainer", [])),
                AppendOp("Variations", CompositeValue("Il2CppMenace.Conversations.ConversationNodeContainer", [])),
            ],
        };
        var patch = WrapInPatch("ConversationTemplate", "x", "m_SerializedNodes", variation);

        CompositeAutoFillers.ApplyPostValidation([patch]);

        var copyCountOps = variation.Operations.Where(o => o.FieldPath == "VariationCopyCount").ToList();
        Assert.Equal(3, copyCountOps.Count);
        Assert.All(copyCountOps, op =>
        {
            Assert.Equal(CompiledTemplateOp.Append, op.Op);
            Assert.Equal(1, op.Value!.Int32);
        });
    }

    [Fact]
    public void PostValidation_VariationCopyCount_RespectsExistingAppends()
    {
        // Two variations + one explicit copy-count append. Filler adds the one
        // missing entry (not all three) so the modder's value at index 0 wins.
        var variation = new CompiledTemplateComposite
        {
            TypeName = "VARIATION",
            Operations =
            [
                AppendOp("Variations", CompositeValue("X", [])),
                AppendOp("Variations", CompositeValue("X", [])),
                AppendOp("VariationCopyCount", Int32Value(5)),
            ],
        };
        var patch = WrapInPatch("ConversationTemplate", "x", "m_SerializedNodes", variation);

        CompositeAutoFillers.ApplyPostValidation([patch]);

        var copyCountOps = variation.Operations.Where(o => o.FieldPath == "VariationCopyCount").ToList();
        Assert.Equal(2, copyCountOps.Count);
        Assert.Equal(5, copyCountOps[0].Value!.Int32);
        Assert.Equal(1, copyCountOps[1].Value!.Int32);
    }

    [Fact]
    public void PostValidation_VariationCopyCount_SkipsWhenCleared()
    {
        // Modder explicitly cleared the parallel array. Filler must not
        // re-populate it (modder may be re-authoring via `set` with `index=`
        // operations, or building a different structure).
        var variation = new CompiledTemplateComposite
        {
            TypeName = "VARIATION",
            Operations =
            [
                AppendOp("Variations", CompositeValue("X", [])),
                AppendOp("Variations", CompositeValue("X", [])),
                new() { Op = CompiledTemplateOp.Clear, FieldPath = "VariationCopyCount" },
            ],
        };
        var patch = WrapInPatch("ConversationTemplate", "x", "m_SerializedNodes", variation);

        CompositeAutoFillers.ApplyPostValidation([patch]);

        var copyCountAppends = variation.Operations
            .Where(o => o.Op == CompiledTemplateOp.Append && o.FieldPath == "VariationCopyCount")
            .ToList();
        Assert.Empty(copyCountAppends);
    }

    [Fact]
    public void PostValidation_RecursesIntoNestedComposites()
    {
        // Outer ACTION composite holds an inner Stem.Sound composite via a
        // nested set op. Filler must reach the inner one for Sound.id derivation.
        var innerSound = new CompiledTemplateComposite
        {
            TypeName = "Stem.Sound",
            Operations = [SetOp("name", StringValue("nested"))],
        };
        var outer = new CompiledTemplateComposite
        {
            TypeName = "SAY",
            Operations =
            [
                SetOp("Sound", CompositeValue("Stem.Sound", innerSound.Operations)),
            ],
        };
        // Plug the actual composite reference so we can inspect it post-walk.
        outer.Operations[0].Value = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Composite,
            Composite = innerSound,
        };
        var patch = WrapInPatch("ConversationTemplate", "x", "m_SerializedNodes", outer);

        CompositeAutoFillers.ApplyPostValidation([patch]);

        Assert.Contains(innerSound.Operations, o => o.FieldPath == "id");
    }

    // --- Helpers ---

    private static CompiledTemplatePatch WrapInPatch(
        string templateType, string templateId, string topField, CompiledTemplateComposite composite)
        => new()
        {
            TemplateType = templateType,
            TemplateId = templateId,
            Set =
            [
                AppendOp(topField, new CompiledTemplateValue
                {
                    Kind = CompiledTemplateValueKind.Composite,
                    Composite = composite,
                }),
            ],
        };

    private static CompiledTemplateSetOperation AppendOp(string fieldPath, CompiledTemplateValue value)
        => new() { Op = CompiledTemplateOp.Append, FieldPath = fieldPath, Value = value };

    private static CompiledTemplateValue StringValue(string v)
        => new() { Kind = CompiledTemplateValueKind.String, String = v };

    private static CompiledTemplateValue Int32Value(int v)
        => new() { Kind = CompiledTemplateValueKind.Int32, Int32 = v };

    private static CompiledTemplateValue CompositeValue(string typeName, List<CompiledTemplateSetOperation> ops)
        => new()
        {
            Kind = CompiledTemplateValueKind.Composite,
            Composite = new CompiledTemplateComposite { TypeName = typeName, Operations = ops },
        };
}
