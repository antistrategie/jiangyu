using Jiangyu.Shared.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Tests <see cref="TemplateReferences"/>, the walk that lists every template a compiled
/// value refers to. A patch block applies only when each of them exists, so a reference
/// nested in a composite must be found as surely as a top-level one.
/// </summary>
public class TemplateReferenceWalkTests
{
    private static CompiledTemplateValue Ref(string? type, string id) => new()
    {
        Kind = CompiledTemplateValueKind.TemplateReference,
        Reference = new CompiledTemplateReference { TemplateType = type, TemplateId = id },
    };

    private static CompiledTemplateSetOperation Op(string field, CompiledTemplateValue? value, CompiledTemplateOp op = CompiledTemplateOp.Set)
        => new() { Op = op, FieldPath = field, Value = value };

    [Fact]
    public void TopLevelReference_IsListed()
    {
        var refs = TemplateReferences.Of(Ref("EntityTemplate", "enemy.pirate")).ToList();

        var only = Assert.Single(refs);
        Assert.Equal("EntityTemplate", only.TemplateType);
        Assert.Equal("enemy.pirate", only.TemplateId);
    }

    [Fact]
    public void ScalarsAndAssets_ListNothing()
    {
        var scalar = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Single, Single = 1f };
        var asset = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.AssetReference, Asset = new CompiledAssetReference() };

        Assert.Empty(TemplateReferences.Of(scalar));
        Assert.Empty(TemplateReferences.Of(asset));
        Assert.Empty(TemplateReferences.Of((CompiledTemplateValue?)null));
        Assert.Empty(TemplateReferences.Of((IEnumerable<CompiledTemplateSetOperation>?)null));
    }

    [Fact]
    public void ReferencesInsideAComposite_AreListedInOrder()
    {
        // The shape of a constructed handler appended to a perk: a whitelist of entity refs.
        var handler = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Composite,
            Composite = new CompiledTemplateComposite
            {
                TypeName = "Buyout",
                Operations =
                [
                    Op("Whitelist", Ref("EntityTemplate", "civilian.worker"), CompiledTemplateOp.Append),
                    Op("Whitelist", Ref("EntityTemplate", "enemy.pirate_scavengers_smg"), CompiledTemplateOp.Append),
                    Op("Cost", new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = 3 }),
                ],
            },
        };
        var ops = new List<CompiledTemplateSetOperation>
        {
            Op("EventHandlers", null, CompiledTemplateOp.Remove),
            Op("EventHandlers", handler, CompiledTemplateOp.Append),
        };

        var ids = TemplateReferences.Of(ops).Select(r => r.TemplateId).ToList();

        Assert.Equal(["civilian.worker", "enemy.pirate_scavengers_smg"], ids);
    }

    [Fact]
    public void NestedCompositesAndTypeConstructions_AreRecursed()
    {
        var inner = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.TypeConstruction,
            TypeConstruction = new CompiledTemplateComposite
            {
                TypeName = "HasTagCondition",
                Operations = [Op("Tag", Ref("TagTemplate", "wmgfl_gift"))],
            },
        };
        var outer = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Composite,
            Composite = new CompiledTemplateComposite
            {
                TypeName = "ConditionalHandler",
                Operations = [Op("Condition", inner), Op("Target", Ref("WeaponTemplate", "weapon.ak15"))],
            },
        };

        var ids = TemplateReferences.Of(outer).Select(r => r.TemplateId).ToList();

        Assert.Equal(["wmgfl_gift", "weapon.ak15"], ids);
    }

    [Fact]
    public void UntypedReference_IsStillListed()
    {
        // A manifest compiled before types were stamped: the walk lists it, the caller
        // decides that it cannot be checked ahead of time.
        var refs = TemplateReferences.Of(Ref(null, "some.id")).ToList();

        Assert.Null(Assert.Single(refs).TemplateType);
    }
}
