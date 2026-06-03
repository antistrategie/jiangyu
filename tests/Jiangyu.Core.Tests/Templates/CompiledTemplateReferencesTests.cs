using System.Linq;
using Jiangyu.Shared.Templates;
using Xunit;

namespace Jiangyu.Core.Tests.Templates;

public class CompiledTemplateReferencesTests
{
    private static CompiledTemplateSetOperation Set(string field, CompiledTemplateValue value)
        => new() { FieldPath = field, Value = value };

    private static CompiledTemplateValue Asset(string name)
        => new() { Asset = new CompiledAssetReference { Name = name } };

    [Fact]
    public void Enumerate_yields_asset_and_qualified_type_references_with_field_paths()
    {
        var patch = new CompiledTemplatePatch
        {
            TemplateType = "EntityTemplate",
            TemplateId = "x",
            Set =
            {
                Set("Icon", Asset("icons/foo")),
                Set("Handlers", new CompiledTemplateValue
                {
                    TypeConstruction = new CompiledTemplateComposite
                    {
                        TypeName = "mod:Outer",
                        Operations =
                        {
                            Set("Inner", new CompiledTemplateValue
                            {
                                Composite = new CompiledTemplateComposite { TypeName = "mod:Nested" },
                            }),
                        },
                    },
                }),
            },
        };

        var refs = CompiledTemplateReferences.Enumerate(patch).ToList();

        Assert.Contains(("asset", "icons/foo", "Icon"), refs);
        Assert.Contains(("type", "mod:Outer", "Handlers"), refs);
        Assert.Contains(("type", "mod:Nested", "Inner"), refs); // nested operations are walked
    }

    [Fact]
    public void Enumerate_skips_unqualified_game_type_names()
    {
        var patch = new CompiledTemplatePatch
        {
            TemplateType = "EntityTemplate",
            TemplateId = "x",
            Set =
            {
                Set("F", new CompiledTemplateValue
                {
                    Composite = new CompiledTemplateComposite { TypeName = "ChangeProperty" }, // no colon
                }),
            },
        };

        Assert.DoesNotContain(CompiledTemplateReferences.Enumerate(patch), r => r.Kind == "type");
    }

    [Fact]
    public void Enumerate_tolerates_a_null_operations_list()
    {
        var patch = new CompiledTemplatePatch
        {
            TemplateType = "EntityTemplate",
            TemplateId = "x",
            Set =
            {
                Set("F", new CompiledTemplateValue
                {
                    // A manifest deserialised with "operations": null must not throw.
                    Composite = new CompiledTemplateComposite { TypeName = "mod:Foo", Operations = null! },
                }),
            },
        };

        var refs = CompiledTemplateReferences.Enumerate(patch).ToList();
        Assert.Contains(("type", "mod:Foo", "F"), refs);
    }
}
