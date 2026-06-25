using System.Linq;
using Jiangyu.Core.Localisation;
using Jiangyu.Shared.Templates;
using Xunit;

namespace Jiangyu.Core.Tests.Localisation;

public class LocalisationCompilerTests
{
    private static CompiledTemplateValue Str(string s)
        => new() { Kind = CompiledTemplateValueKind.String, String = s };

    private static CompiledTemplateSetOperation DescentDefaultTranslation(string field, string value)
        => new()
        {
            Op = CompiledTemplateOp.Set,
            FieldPath = "m_DefaultTranslation",
            Descent = [new TemplateDescentStep { Field = field }],
            Value = Str(value),
        };

    [Fact]
    public void ExtractCatalogue_KeysTopLevelAndNestedLocalisedFields()
    {
        var manifest = new CompiledTemplatePatchManifest
        {
            TemplatePatches =
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "WeaponTemplate",
                    TemplateId = "weapon.ak15",
                    Set =
                    [
                        DescentDefaultTranslation("Title", "Kalashnikova-15"),
                        // A non-localised write is ignored.
                        new CompiledTemplateSetOperation { Op = CompiledTemplateOp.Set, FieldPath = "Model", Value = Str("weapon/ak15/main") },
                        // A deeper localised write is keyed by its full descent path.
                        new CompiledTemplateSetOperation
                        {
                            Op = CompiledTemplateOp.Set,
                            FieldPath = "m_DefaultTranslation",
                            Descent = [new TemplateDescentStep { Field = "EmotionalStates" }, new TemplateDescentStep { Field = "Response", Index = 0 }],
                            Value = Str("deep line"),
                        },
                    ],
                },
            ],
        };

        var po = LocalisationCompiler.ExtractCatalogue(manifest, "WOMENACE", out var skipped);

        Assert.Equal(0, skipped);
        Assert.Contains(po.Entries, e => e.Context == "WOMENACE::WeaponTemplate/weapon.ak15/Title" && e.Id == "Kalashnikova-15");
        Assert.Contains(po.Entries, e => e.Context == "WOMENACE::WeaponTemplate/weapon.ak15/EmotionalStates/Response[0]" && e.Id == "deep line");
    }

    [Fact]
    public void ExtractCatalogue_HandlesReplaceFormComposite()
    {
        var manifest = new CompiledTemplatePatchManifest
        {
            TemplatePatches =
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "WeaponTemplate",
                    TemplateId = "weapon.ak15",
                    Set =
                    [
                        new CompiledTemplateSetOperation
                        {
                            Op = CompiledTemplateOp.Set,
                            FieldPath = "Title",
                            Value = new CompiledTemplateValue
                            {
                                Kind = CompiledTemplateValueKind.Composite,
                                Composite = new CompiledTemplateComposite
                                {
                                    TypeName = "LocalizedLine",
                                    Operations = [new CompiledTemplateSetOperation { Op = CompiledTemplateOp.Set, FieldPath = "m_DefaultTranslation", Value = Str("Replaced") }],
                                },
                            },
                        },
                    ],
                },
            ],
        };

        var entry = Assert.Single(LocalisationCompiler.ExtractCatalogue(manifest, "WOMENACE", out _).Entries);
        Assert.Equal("WOMENACE::WeaponTemplate/weapon.ak15/Title", entry.Context);
        Assert.Equal("Replaced", entry.Id);
    }

    [Fact]
    public void ExtractUiKeys_FindsLiteralLocaleTextCalls()
    {
        const string source = """var b = new TextButton(Locale.Text("WOMENACE::ui/swap_form", "SWAP FORM"));""";
        var keys = LocalisationCompiler.ExtractUiKeys(source).ToList();
        Assert.Contains(("WOMENACE::ui/swap_form", "SWAP FORM"), keys);
    }

    [Fact]
    public void ExtractUiKeys_FindsDeclarativeLocalisedTextLiterals()
    {
        // A data-table entry: the runtime read uses computed args, but the literal declaration is
        // extractable so the string still reaches translators.
        const string source =
            """new Entry { Title = new LocalisedText("WOMENACE::ui/affinity/wmgfl_voymastina/lv2", "Outfit(s): Erwin") };""";
        var keys = LocalisationCompiler.ExtractUiKeys(source).ToList();
        Assert.Contains(("WOMENACE::ui/affinity/wmgfl_voymastina/lv2", "Outfit(s): Erwin"), keys);
    }

    [Fact]
    public void ExtractUxmlUiKeys_FindsMarkedLabels_AndIgnoresHyphenatedNameAttributes()
    {
        const string uxml = """
            <ui:Label name="@WOMENACE::ui/give_gifts" text="GIVE GIFTS" />
            <ui:Label data-name="@nope" text="X" />
            """;
        var keys = LocalisationCompiler.ExtractUxmlUiKeys(uxml).ToList();
        Assert.Contains(("WOMENACE::ui/give_gifts", "GIVE GIFTS"), keys);
        Assert.DoesNotContain(keys, k => k.Key == "nope");
    }
}
