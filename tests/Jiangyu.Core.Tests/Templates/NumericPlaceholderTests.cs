using System.Globalization;
using Jiangyu.Core.Templates;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Tests.Templates;

public class NumericPlaceholderTests
{
    [Theory]
    [InlineData("number", 5f, "5")]
    [InlineData("percent", .75f, "75")]
    [InlineData("bonus-percent", 1.08f, "8")]
    [InlineData("bonus-percent", 1.13f, "13")]
    [InlineData("reduction-percent", .875f, "12.5")]
    [InlineData("reduction-percent", .8f, "20")]
    [InlineData("magnitude", -80f, "80")]
    [InlineData("number", -.00001f, "0")]
    public void FormatsWithoutSinglePrecisionNoise(string format, float source, string expected)
    {
        var binding = new NumericPlaceholderBinding { Format = format };
        Assert.True(binding.TryFormat(source, out var text));
        Assert.Equal(expected, text);
    }

    [Fact]
    public void FormatsIndependentlyOfProcessCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.True(new NumericPlaceholderBinding().TryFormat(12.5f, out var text));
            Assert.Equal("12.5", text);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("13")]
    [InlineData(true)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void RejectsNonNumericAndNonFiniteValues(object source)
        => Assert.False(new NumericPlaceholderBinding().TryFormat(source, out _));

    [Fact]
    public void EditorAndCompiledJsonPreserveBinding()
    {
        var doc = KdlTemplateParser.ParseText("""
            patch "SkillTemplate" "effect.test" {
                set "Description" {
                    clear "m_Placeholders"
                    append "m_Placeholders" bind="SkillTemplate" "effect.source" path="EventHandlers[0].AmountMult" format="bonus-percent"
                }
            }
            """);
        Assert.Empty(doc.Errors);
        var emitted = KdlTemplateSerialiser.Serialise(doc);
        var roundTrip = KdlTemplateParser.ParseText(emitted);
        Assert.Empty(roundTrip.Errors);
        var op = KdlEditorBridge.EditorDirectiveToCompiled(roundTrip.Nodes[0].Directives[1]);
        var binding = op.Value!.NumericPlaceholder!;
        Assert.Equal("EventHandlers[0].AmountMult", binding.Path);
        Assert.Equal("bonus-percent", binding.Format);
        Assert.Equal("effect.source", binding.Source.TemplateId);
        Assert.Equal("SkillTemplate", binding.Source.TemplateType);
        Assert.Equal("Description", Assert.Single(op.Descent!).Field);
        Assert.Same(binding.Source, Assert.Single(TemplateReferences.Of(op.Value)));

        var manifest = new CompiledTemplatePatchManifest
        {
            TemplatePatches = [new() { TemplateId = "effect.test", Set = [op] }],
        };
        var loaded = CompiledTemplatePatchManifest.FromJson(manifest.ToJson());
        Assert.Equal(binding.Path, loaded.TemplatePatches![0].Set[0].Value!.NumericPlaceholder!.Path);
        Assert.True(TemplatePatchPathValidator.IsSupportedValue(op.Value));
    }

    [Theory]
    [InlineData("bind=\"SkillTemplate\" \"effect.test\"")]
    [InlineData("bind=\"\" \"effect.test\" path=\"Uses\"")]
    [InlineData("bind=\"SkillTemplate\" \"\" path=\"Uses\"")]
    [InlineData("bind=\"SkillTemplate\" \"effect.test\" path=\"Uses()\"")]
    [InlineData("bind=\"SkillTemplate\" \"effect.test\" path=\"Items[-1].Amount\"")]
    [InlineData("bind=\"SkillTemplate\" \"effect.test\" path=\"Uses\" format=\"expression\"")]
    [InlineData("bind=\"SkillTemplate\" \"effect.test\" path=\"Uses\" format=1")]
    [InlineData("bind=\"SkillTemplate\" \"effect.test\" path=\"Uses\" ref=\"SkillTemplate\"")]
    [InlineData("bind=\"SkillTemplate\" \"effect.test\" path=\"Uses\" {\n set \"Amount\" 1\n }")]
    [InlineData("\"literal\" format=\"percent\"")]
    public void RejectsMalformedBindings(string value)
    {
        var doc = KdlTemplateParser.ParseText($"patch \"SkillTemplate\" \"effect.test\" {{\n append \"m_Placeholders\" {value}\n}}");
        Assert.NotEmpty(doc.Errors);
    }

    [Theory]
    [InlineData("Count", true)]
    [InlineData("Multiplier", true)]
    [InlineData("Amounts[0]", true)]
    [InlineData("Missing", false)]
    [InlineData("Text", false)]
    [InlineData("Description", false)]
    [InlineData("Amounts", false)]
    public void ValidatesNumericSource(string path, bool valid)
    {
        using var catalog = TemplateTypeCatalog.Load(typeof(NumericPlaceholderTests).Assembly.Location);
        var doc = KdlTemplateParser.ParseText($$"""
            patch "PlaceholderFixture" "test" {
                set "Description" {
                    append "m_Placeholders" bind="PlaceholderFixture" "source" path="{{path}}"
                }
            }
            """);
        TemplateCatalogValidator.ValidateEditorDocument(doc, catalog);
        Assert.Equal(valid, doc.Errors.Count == 0);
    }

    [Theory]
    [InlineData("append", "")]
    [InlineData("insert", "index=0")]
    [InlineData("set", "index=0")]
    public void DottedDestinationRoundTripsAndValidates(string operation, string index)
    {
        using var catalog = TemplateTypeCatalog.Load(typeof(NumericPlaceholderTests).Assembly.Location);
        var doc = KdlTemplateParser.ParseText($$"""
            patch "PlaceholderFixture" "test" {
                {{operation}} "Description.m_Placeholders" {{index}} bind="PlaceholderFixture" "source" path="Count"
            }
            """);
        var roundTrip = KdlTemplateParser.ParseText(KdlTemplateSerialiser.Serialise(doc));
        Assert.Empty(doc.Errors);
        TemplateCatalogValidator.ValidateEditorDocument(roundTrip, catalog);
        Assert.Empty(roundTrip.Errors);
        var op = KdlEditorBridge.EditorDirectiveToCompiled(Assert.Single(roundTrip.Nodes[0].Directives));
        Assert.Equal("Description.m_Placeholders", op.FieldPath);
        Assert.Equal("Count", op.Value!.NumericPlaceholder!.Path);
    }

    [Theory]
    [InlineData("", "source", "Count", "number")]
    [InlineData("PlaceholderFixture", "", "Count", "number")]
    [InlineData("PlaceholderFixture", "source", "", "number")]
    [InlineData("PlaceholderFixture", "source", "Count", "unknown")]
    public void IncompleteEditorBindingsReportInvalidSource(string type, string id, string path, string format)
    {
        using var catalog = TemplateTypeCatalog.Load(typeof(NumericPlaceholderTests).Assembly.Location);
        var doc = KdlTemplateParser.ParseText("""
            patch "PlaceholderFixture" "test" {
                append "Description.m_Placeholders" "value"
            }
            """);
        doc.Nodes[0].Directives[0].Value = new KdlEditorValue
        {
            Kind = KdlEditorValueKind.NumericPlaceholder,
            ReferenceType = type,
            ReferenceId = id,
            BindingPath = path,
            BindingFormat = format,
        };
        TemplateCatalogValidator.ValidateEditorDocument(doc, catalog);
        Assert.Contains(doc.Errors, error => error.Message.Contains("bind= requires"));
        Assert.DoesNotContain(doc.Errors, error => error.Message.Contains("only supported on entries"));
    }

    [Fact]
    public void RejectsBindingOnAnOrdinaryStringField()
    {
        using var catalog = TemplateTypeCatalog.Load(typeof(NumericPlaceholderTests).Assembly.Location);
        var doc = KdlTemplateParser.ParseText("""
            patch "PlaceholderFixture" "test" {
                set "Text" bind="PlaceholderFixture" "source" path="Count"
            }
            """);
        TemplateCatalogValidator.ValidateEditorDocument(doc, catalog);
        Assert.Contains(doc.Errors, error => error.Message.Contains("m_Placeholders"));
    }
}

public class PlaceholderFixture : Menace.Tools.DataTemplate
{
    public PlaceholderMultiline Description { get; set; } = new();
    public int Count { get; set; }
    public float Multiplier { get; set; }
    public int[] Amounts { get; set; } = [];
    public string Text { get; set; } = string.Empty;
}

public class BaseLocalizedString
{
    // The fixture must match the native localisation member name.
#pragma warning disable IDE1006
    public string[] m_Placeholders { get; set; } = [];
#pragma warning restore IDE1006
}

public class PlaceholderMultiline : BaseLocalizedString
{
}
