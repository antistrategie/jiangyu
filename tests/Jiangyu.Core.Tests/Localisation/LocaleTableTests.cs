using System.Linq;
using Jiangyu.Core.Localisation;
using Jiangyu.Shared.Localisation;
using Jiangyu.Shared.Templates;
using Xunit;

namespace Jiangyu.Core.Tests.Localisation;

public class LocaleTableTests
{
    private const string SamplePo = """
        msgctxt "WOMENACE::WeaponTemplate/weapon.ak15/Title"
        msgid "Kalashnikova-15"
        msgstr "Kalachnikova-15"

        msgctxt "WOMENACE::WeaponTemplate/weapon.ak15/ShortName"
        msgid "Assault Rifle"
        msgstr ""

        #, fuzzy
        msgctxt "WOMENACE::WeaponTemplate/weapon.ak15/Description"
        msgid "old source"
        msgstr "stale translation"

        msgctxt "WOMENACE::ui/swap_form"
        msgid "SWAP FORM"
        msgstr "CHANGER DE FORME"
        """;

    [Fact]
    public void Compile_BuildsTranslations_DropsEmptyAndFuzzy_AndSeparatesUi()
    {
        var result = LocaleTable.Compile(SamplePo);

        var patch = Assert.Single(result.Translations.TemplatePatches!);
        Assert.Equal("WeaponTemplate", patch.TemplateType);
        Assert.Equal("weapon.ak15", patch.TemplateId);
        var op = Assert.Single(patch.Set);
        Assert.Equal("m_DefaultTranslation", op.FieldPath);
        Assert.Equal("Title", op.Descent!.Single().Field);
        Assert.Equal("Kalachnikova-15", op.Value!.String);

        Assert.Equal("CHANGER DE FORME", Assert.Contains("WOMENACE::ui/swap_form", result.Ui));
        Assert.Equal(0, result.Malformed);
    }

    [Fact]
    public void Compile_BaselineCoversEveryTemplateEntry_EvenUntranslated()
    {
        var result = LocaleTable.Compile(SamplePo);

        // Baseline carries the English source (msgid) for all three template fields, including the
        // empty and fuzzy ones, so a switch reverts them. UI entries are not part of the baseline.
        var baseline = Assert.Single(result.Baseline.TemplatePatches!).Set;
        Assert.Equal(3, baseline.Count);
        Assert.Contains(baseline, o => o.Value!.String == "Kalashnikova-15");
        Assert.Contains(baseline, o => o.Value!.String == "Assault Rifle");
        Assert.Contains(baseline, o => o.Value!.String == "old source");
    }

    [Fact]
    public void Compile_CountsMalformedKeys()
    {
        var result = LocaleTable.Compile("""
            msgctxt "no-separator-here"
            msgid "x"
            msgstr "y"
            """);

        Assert.Empty(result.Translations.TemplatePatches!);
        Assert.Equal(1, result.Malformed);
    }

    [Fact]
    public void Compile_NestedDescentRoundTripsFromCatalogue()
    {
        var manifest = new CompiledTemplatePatchManifest
        {
            TemplatePatches =
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "SpeakerTemplate",
                    TemplateId = "spk",
                    Set =
                    [
                        new CompiledTemplateSetOperation
                        {
                            Op = CompiledTemplateOp.Set,
                            FieldPath = "m_DefaultTranslation",
                            Descent = [new TemplateDescentStep { Field = "EmotionalStates", Index = 3 }, new TemplateDescentStep { Field = "Response" }],
                            Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.String, String = "Original line" },
                        },
                    ],
                },
            ],
        };

        // Emit the POT (compiler), fill the translation, write it, then compile it back (loader path).
        var pot = LocalisationCompiler.ExtractCatalogue(manifest, "WOMENACE", out _);
        var entry = Assert.Single(pot.Entries);
        Assert.Equal("WOMENACE::SpeakerTemplate/spk/EmotionalStates[3]/Response", entry.Context);
        entry.Str = "Ligne traduite";

        var op = LocaleTable.Compile(PoFormat.Write(pot)).Translations.TemplatePatches!.Single().Set.Single();
        Assert.Equal("m_DefaultTranslation", op.FieldPath);
        Assert.Equal(2, op.Descent!.Count);
        Assert.Equal("EmotionalStates", op.Descent[0].Field);
        Assert.Equal(3, op.Descent[0].Index);
        Assert.Equal("Response", op.Descent[1].Field);
        Assert.Null(op.Descent[1].Index);
        Assert.Equal("Ligne traduite", op.Value!.String);
    }

    [Fact]
    public void Compile_ConversationSubtitle_RoundTripsFromNestedSayNode()
    {
        // A SAY node's Text lives deep in the node tree (Variation -> container -> say). Extraction
        // recurses to it and keys the entry by the node's deterministic guid; the loader path parses
        // that back to a conversation op carrying the guid.
        var say = new CompiledTemplateComposite
        {
            TypeName = "Il2CppMenace.Conversations.SayConversationNode",
            Operations =
            [
                new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Set, FieldPath = "Guid",
                    Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = 1148226405 },
                },
                new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Set, FieldPath = "Text",
                    Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.String, String = "Need backup!" },
                },
            ],
        };
        var container = new CompiledTemplateComposite
        {
            TypeName = "Il2CppMenace.Conversations.ConversationNodeContainer",
            Operations =
            [
                new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Append, FieldPath = "m_SerializedNodes",
                    Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Composite, Composite = say },
                },
            ],
        };
        var manifest = new CompiledTemplatePatchManifest
        {
            TemplatePatches =
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "ConversationTemplate",
                    TemplateId = "Cheyanne/click_bark",
                    Set =
                    [
                        new CompiledTemplateSetOperation
                        {
                            Op = CompiledTemplateOp.Append, FieldPath = "m_SerializedNodes",
                            Descent = [new TemplateDescentStep { Field = "Nodes" }],
                            Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Composite, Composite = container },
                        },
                    ],
                },
            ],
        };

        var pot = LocalisationCompiler.ExtractCatalogue(manifest, "WOMENACE", out _);
        var entry = Assert.Single(pot.Entries);
        Assert.Equal("WOMENACE::conv/Cheyanne/click_bark/1148226405", entry.Context);
        Assert.Equal("Need backup!", entry.Id);
        entry.Str = "Besoin de renforts !";

        var result = LocaleTable.Compile(PoFormat.Write(pot));
        Assert.Empty(result.Translations.TemplatePatches!);   // not a template-field op
        var conv = Assert.Single(result.ConversationTranslations);
        Assert.Equal("Cheyanne/click_bark", conv.ConvId);
        Assert.Equal(1148226405, conv.NodeGuid);
        Assert.Equal("Besoin de renforts !", conv.Value);

        // Baseline carries the English source for the revert path, even before translation.
        var baseline = Assert.Single(result.ConversationBaseline);
        Assert.Equal("Need backup!", baseline.Value);
    }
}
