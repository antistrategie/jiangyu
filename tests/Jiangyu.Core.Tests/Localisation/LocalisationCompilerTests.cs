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

    private static CompiledTemplateSetOperation Line(string field, string value)
        => new()
        {
            Op = CompiledTemplateOp.Set,
            FieldPath = field,
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Composite,
                Composite = new CompiledTemplateComposite
                {
                    TypeName = "LocalizedLine",
                    Operations = [new CompiledTemplateSetOperation { Op = CompiledTemplateOp.Set, FieldPath = "m_DefaultTranslation", Value = Str(value) }],
                },
            },
        };

    private static CompiledTemplateSetOperation Append(string field, params CompiledTemplateSetOperation[] inner)
        => new()
        {
            Op = CompiledTemplateOp.Append,
            FieldPath = field,
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Composite,
                Composite = new CompiledTemplateComposite { TypeName = "EmotionalStateResponse", Operations = [.. inner] },
            },
        };

    [Fact]
    public void ExtractCatalogue_KeysAppendedElementsFromTheEnd()
    {
        // clear + 3 appends: the elements land at 0, 1, 2, so they are 3, 2 and 1 back from the end.
        var manifest = new CompiledTemplatePatchManifest
        {
            TemplatePatches =
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "UnitLeaderTemplate",
                    TemplateId = "squad_leader.asteria",
                    Set =
                    [
                        new CompiledTemplateSetOperation { Op = CompiledTemplateOp.Clear, FieldPath = "EmotionalStateResponses" },
                        Append("EmotionalStateResponses", Line("Text", "first")),
                        Append("EmotionalStateResponses", Line("Text", "second")),
                        Append("EmotionalStateResponses", Line("Text", "third")),
                    ],
                },
            ],
        };

        var po = LocalisationCompiler.ExtractCatalogue(manifest, "WOMENACE", out var skipped);

        Assert.Equal(0, skipped);
        Assert.Equal(3, po.Entries.Count);
        Assert.Contains(po.Entries, e => e.Context == "WOMENACE::UnitLeaderTemplate/squad_leader.asteria/EmotionalStateResponses[^3]/Text" && e.Id == "first");
        Assert.Contains(po.Entries, e => e.Context == "WOMENACE::UnitLeaderTemplate/squad_leader.asteria/EmotionalStateResponses[^2]/Text" && e.Id == "second");
        Assert.Contains(po.Entries, e => e.Context == "WOMENACE::UnitLeaderTemplate/squad_leader.asteria/EmotionalStateResponses[^1]/Text" && e.Id == "third");
    }

    [Fact]
    public void ExtractCatalogue_AppendsOntoAnExistingListStayFromEnd()
    {
        // No clear: the mod's two tooltips land after however many the game already has, so only
        // their distance from the end is knowable here.
        var manifest = new CompiledTemplatePatchManifest
        {
            TemplatePatches =
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "TextTooltipsConfig",
                    TemplateId = "text_tooltips_config",
                    Set =
                    [
                        Append("Tooltips", Line("TooltipHeading", "Burn"), Line("TooltipText", "Sets the target alight.")),
                        Append("Tooltips", Line("TooltipHeading", "Freeze"), Line("TooltipText", "Slows the target.")),
                    ],
                },
            ],
        };

        var po = LocalisationCompiler.ExtractCatalogue(manifest, "WOMENACE", out var skipped);

        Assert.Equal(0, skipped);
        Assert.Contains(po.Entries, e => e.Context == "WOMENACE::TextTooltipsConfig/text_tooltips_config/Tooltips[^2]/TooltipHeading" && e.Id == "Burn");
        Assert.Contains(po.Entries, e => e.Context == "WOMENACE::TextTooltipsConfig/text_tooltips_config/Tooltips[^2]/TooltipText" && e.Id == "Sets the target alight.");
        Assert.Contains(po.Entries, e => e.Context == "WOMENACE::TextTooltipsConfig/text_tooltips_config/Tooltips[^1]/TooltipHeading" && e.Id == "Freeze");
    }

    [Fact]
    public void ExtractCatalogue_CountsAppendsAcrossEveryBlockTargetingOneTemplate()
    {
        // The loader merges every patch block for a template into one operation stream, so two blocks
        // appending to the same collection land one after the other. Counting per block would place
        // the first block's element one from the end, where the second block's element actually sits.
        var manifest = new CompiledTemplatePatchManifest
        {
            TemplatePatches =
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "TextTooltipsConfig",
                    TemplateId = "text_tooltips_config",
                    Set = [Append("Tooltips", Line("TooltipHeading", "Burn"))],
                },
                new CompiledTemplatePatch
                {
                    TemplateType = "TextTooltipsConfig",
                    TemplateId = "text_tooltips_config",
                    Set = [Append("Tooltips", Line("TooltipHeading", "Freeze"))],
                },
            ],
        };

        var po = LocalisationCompiler.ExtractCatalogue(manifest, "WOMENACE", out var skipped);

        Assert.Equal(0, skipped);
        Assert.Contains(po.Entries, e => e.Context == "WOMENACE::TextTooltipsConfig/text_tooltips_config/Tooltips[^2]/TooltipHeading" && e.Id == "Burn");
        Assert.Contains(po.Entries, e => e.Context == "WOMENACE::TextTooltipsConfig/text_tooltips_config/Tooltips[^1]/TooltipHeading" && e.Id == "Freeze");
    }

    [Fact]
    public void ExtractCatalogue_NamesTheElementAnIndexedSetReplaces()
    {
        // KDL `set "Tooltips" index=2 type="Tooltip" { ... }` builds a fresh element and writes it to
        // slot 2. Dropping the index would key the entry as if Tooltips were an object, and the
        // descent would then fail to navigate at runtime.
        var manifest = new CompiledTemplatePatchManifest
        {
            TemplatePatches =
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "TextTooltipsConfig",
                    TemplateId = "text_tooltips_config",
                    Set =
                    [
                        new CompiledTemplateSetOperation
                        {
                            Op = CompiledTemplateOp.Set,
                            FieldPath = "Tooltips",
                            Index = 2,
                            Value = new CompiledTemplateValue
                            {
                                Kind = CompiledTemplateValueKind.Composite,
                                Composite = new CompiledTemplateComposite
                                {
                                    TypeName = "Tooltip",
                                    Operations = [Line("TooltipText", "Sets the target alight.")],
                                },
                            },
                        },
                    ],
                },
            ],
        };

        var entry = Assert.Single(LocalisationCompiler.ExtractCatalogue(manifest, "WOMENACE", out _).Entries);
        Assert.Equal("WOMENACE::TextTooltipsConfig/text_tooltips_config/Tooltips[2]/TooltipText", entry.Context);
    }

    [Fact]
    public void ExtractCatalogue_GivesUpOnAppendsAShiftingOpWouldMove()
    {
        // A Remove after the appends moves what is already placed, so a from-end position would name
        // the wrong element. The strings are reported as skipped rather than mis-addressed.
        var manifest = new CompiledTemplatePatchManifest
        {
            TemplatePatches =
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "UnitLeaderTemplate",
                    TemplateId = "squad_leader.asteria",
                    Set =
                    [
                        Append("EmotionalStateResponses", Line("Text", "first")),
                        new CompiledTemplateSetOperation { Op = CompiledTemplateOp.Remove, FieldPath = "EmotionalStateResponses", Index = 0 },
                    ],
                },
            ],
        };

        var po = LocalisationCompiler.ExtractCatalogue(manifest, "WOMENACE", out var skipped);

        Assert.Empty(po.Entries);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void ExtractCatalogue_ReachesLocalisedTextNestedBelowTheReplacedField()
    {
        // A composite that is not itself a line but carries one deeper still reaches the POT.
        var manifest = new CompiledTemplatePatchManifest
        {
            TemplatePatches =
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "SkillTemplate",
                    TemplateId = "active.sextans_slash",
                    Set =
                    [
                        new CompiledTemplateSetOperation
                        {
                            Op = CompiledTemplateOp.Set,
                            FieldPath = "Highlight",
                            Value = new CompiledTemplateValue
                            {
                                Kind = CompiledTemplateValueKind.Composite,
                                Composite = new CompiledTemplateComposite
                                {
                                    TypeName = "SkillHighlight",
                                    Operations = [Line("Caption", "Bleeds the target.")],
                                },
                            },
                        },
                    ],
                },
            ],
        };

        var entry = Assert.Single(LocalisationCompiler.ExtractCatalogue(manifest, "WOMENACE", out _).Entries);
        Assert.Equal("WOMENACE::SkillTemplate/active.sextans_slash/Highlight/Caption", entry.Context);
        Assert.Equal("Bleeds the target.", entry.Id);
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
