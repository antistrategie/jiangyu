using Jiangyu.Loader.Logging;
using Jiangyu.Loader.Runtime.Localisation;
using Jiangyu.Loader.Templates;
using Jiangyu.Shared.Bundles;
using Jiangyu.Shared.Localisation;
using Jiangyu.Shared.Templates;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Tests the catalogue's one-entry-per-type filing and the alias merge behind a block: a
/// qualified and a short spelling share an entry, and one mod's ops under a type and its
/// ancestors come out as one load-ordered stream.
/// </summary>
public class TemplatePatchCatalogAliasTests
{
    private static DiscoveredMod Mod(string name) =>
        new(name, "", null, null, "", "", "", new List<string>(), new List<ManifestDependency>(), new List<ManifestDependency>());

    private static CompiledTemplateSetOperation Set(string field, int value) => new()
    {
        Op = CompiledTemplateOp.Set,
        FieldPath = field,
        Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = value },
    };

    private static CompiledTemplateSetOperation Append(string field) => new()
    {
        Op = CompiledTemplateOp.Append,
        FieldPath = field,
        Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = 0 },
    };

    private static CompiledTemplatePatchManifest Manifest(params CompiledTemplatePatch[] patches) => new() { TemplatePatches = [.. patches] };

    private static CompiledTemplatePatch Patch(string type, string id, params CompiledTemplateSetOperation[] ops)
        => new() { TemplateType = type, TemplateId = id, Set = [.. ops] };

    // Stands in for the runtime: Menace.WeaponTemplate spells WeaponTemplate, which derives from BaseItemTemplate.
    private static string Canonical(string name) => name == "Menace.WeaponTemplate" ? "WeaponTemplate" : name;

    private static bool SameSpace(string a, string b)
        => a == b || (a, b) is ("WeaponTemplate", "BaseItemTemplate") or ("BaseItemTemplate", "WeaponTemplate");

    private static TemplatePatchCatalog Load(params (string Mod, CompiledTemplatePatchManifest Templates)[] mods)
    {
        var catalog = new TemplatePatchCatalog(SameSpace, Canonical);
        catalog.Load([.. mods.Select(m => (Mod(m.Mod), m.Templates))], new LoaderLog(null!));
        return catalog;
    }

    [Fact]
    public void Load_FilesEverySpellingOfATypeUnderItsCanonicalName()
    {
        var catalog = Load(
            ("A", Manifest(Patch("Menace.WeaponTemplate", "w", Set("Damage", 1)))),
            ("B", Manifest(Patch("WeaponTemplate", "w", Set("Range", 2)))));

        var entry = Assert.Single(catalog.EnumerateByType());
        Assert.Equal("WeaponTemplate", entry.Key);
        Assert.True(catalog.TryGetOperations("Menace.WeaponTemplate", "w", out var ops));
        Assert.Equal(["A", "B"], ops!.Select(op => op.OwnerLabel));
        Assert.Equal(["WeaponTemplate"], catalog.AliasNames("Menace.WeaponTemplate"));
        Assert.Equal("WeaponTemplate", catalog.EntryNameFor("Menace.WeaponTemplate"));
    }

    [Fact]
    public void OperationsAcrossAliases_MergesAncestorAndDescendantEntriesInLoadOrder_Once()
    {
        var catalog = Load(
            ("A", Manifest(Patch("WeaponTemplate", "w", Set("Damage", 1)), Patch("BaseItemTemplate", "w", Set("Weight", 1)))),
            ("B", Manifest(Patch("BaseItemTemplate", "w", Append("Tags")))),
            ("C", Manifest(Patch("Menace.WeaponTemplate", "w", Set("Damage", 3)))));

        var ops = catalog.OperationsAcrossAliases("BaseItemTemplate", "w");
        Assert.Equal(["A", "A", "B", "C"], ops.Select(op => op.OwnerLabel));
        Assert.Equal(["Damage", "Weight", "Tags", "Damage"], ops.Select(op => op.FieldPath));
        Assert.Equal(ops.Select(op => op.Sequence).OrderBy(s => s), ops.Select(op => op.Sequence));
        Assert.Equal(ops.Select(op => op.Sequence), catalog.OperationsAcrossAliases("Menace.WeaponTemplate", "w").Select(op => op.Sequence));
        Assert.True(new HashSet<string> { "Damage", "Weight", "Tags" }.SetEquals(catalog.TouchedTopLevelFields("WeaponTemplate", "w")));

        // Two entries, each once, whichever name asks.
        Assert.Equal(2, catalog.AliasNames("Menace.WeaponTemplate").Count);
        Assert.Equal(2, catalog.AliasNames("BaseItemTemplate").Count);
    }

    [Fact]
    public void Load_KeepsAnOverriddenSet_InLoadOrder_AsTwoBlocks()
    {
        var catalog = Load(
            ("A", Manifest(Patch("WeaponTemplate", "w", Set("Damage", 1)))),
            ("B", Manifest(Patch("WeaponTemplate", "w", Set("Damage", 2)))));

        Assert.True(catalog.TryGetOperations("WeaponTemplate", "w", out var ops));
        Assert.Equal(2, ops!.Count);
        Assert.Equal(2, catalog.PatchCount);
        Assert.True(ops[1].Sequence > ops[0].Sequence);
        Assert.Equal(["A", "B"], TemplatePatchApplier.SplitBlocks(ops).Select(b => b.Owner));
    }
}
