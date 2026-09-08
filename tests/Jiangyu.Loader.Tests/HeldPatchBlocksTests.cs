using Jiangyu.Loader.Templates;
using Jiangyu.Shared.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Tests the held-block bookkeeping (<see cref="HeldPatchBlocks"/>) and the split of a
/// template's merged op stream into one block per mod (<see cref="TemplatePatchApplier.SplitBlocks"/>).
/// </summary>
public class HeldPatchBlocksTests
{
    private static LoadedPatchOperation Op(string owner, string field = "Damage")
        => new(CompiledTemplateOp.Set, field, null, null, null,
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Single, Single = 1f }, owner);

    private static HeldPatchBlock Block(string type, string id, string owner, int ops, params string[] missing)
        => new(type, id, owner, [.. Enumerable.Range(0, ops).Select(_ => Op(owner))], [.. missing.Select(Ref)]);

    private static TemplateRef Ref(string key)
    {
        var colon = key.IndexOf(':');
        return new TemplateRef(key[..colon], key[(colon + 1)..]);
    }

    [Fact]
    public void SplitBlocks_OneBlockPerMod_InFirstAppearanceOrder()
    {
        List<LoadedPatchOperation> ops = [Op("mod-a"), Op("mod-a"), Op("mod-b"), Op("mod-a")];

        var blocks = TemplatePatchApplier.SplitBlocks(ops);

        Assert.Equal(["mod-a", "mod-b"], blocks.Select(b => b.Owner));
        Assert.Equal([3, 1], blocks.Select(b => b.Ops.Count));
        Assert.Same(ops[0], blocks[0].Ops[0]);
        Assert.Same(ops[3], blocks[0].Ops[2]);
        Assert.Same(ops[2], blocks[1].Ops[0]);
    }

    [Fact]
    public void SplitBlocks_EmptyStream_NoBlocks()
    {
        Assert.Empty(TemplatePatchApplier.SplitBlocks([]));
    }

    [Fact]
    public void Add_KeepsTheFirstBlockPerModAndTemplate()
    {
        var held = new HeldPatchBlocks((a, b) => a == b);

        Assert.True(held.Add(Block("PerkTemplate", "perk.bribe", "mercs", 2, "EntityTemplate:enemy.smg")));
        Assert.False(held.Add(Block("PerkTemplate", "perk.bribe", "mercs", 5, "EntityTemplate:other")));
        Assert.True(held.Add(Block("PerkTemplate", "perk.bribe", "womenace", 1, "TagTemplate:t")));

        Assert.Equal(2, held.Count);
        Assert.Equal(3, held.PendingOps);
        Assert.True(held.Contains("PerkTemplate", "perk.bribe", "mercs"));
        Assert.False(held.Contains("PerkTemplate", "perk.bribe", "other"));
        Assert.True(held.ContainsTemplate(type => type == "PerkTemplate", "perk.bribe"));
        Assert.True(held.Contains("mercs", type => type == "PerkTemplate", "perk.bribe"));
        Assert.False(held.Contains("other", type => type == "PerkTemplate", "perk.bribe"));
    }

    [Fact]
    public void Add_TreatsAnAncestorNameAsTheSameTemplate()
    {
        static bool Hierarchy(string a, string b) => a == b
            || (a, b) is ("WeaponTemplate", "BaseItemTemplate") or ("BaseItemTemplate", "WeaponTemplate");
        var held = new HeldPatchBlocks(Hierarchy);

        Assert.True(held.Add(Block("BaseItemTemplate", "weapon.a", "m", 2, "EntityTemplate:x")));
        Assert.False(held.Add(Block("WeaponTemplate", "weapon.a", "m", 2, "EntityTemplate:x")));
        Assert.True(held.Add(Block("PerkTemplate", "weapon.a", "m", 1, "EntityTemplate:x")));
        Assert.Equal(2, held.Count);
        Assert.True(held.Contains("WeaponTemplate", "weapon.a", "m"));
        Assert.False(held.Contains("WeaponTemplate", "weapon.a", "other"));
    }

    [Fact]
    public void Remove_ReleasesOnlyThatBlock()
    {
        var held = new HeldPatchBlocks((a, b) => a == b);
        var mercs = Block("PerkTemplate", "perk.bribe", "mercs", 2, "EntityTemplate:enemy.smg");
        var other = Block("PerkTemplate", "perk.bribe", "womenace", 1, "TagTemplate:t");
        held.Add(mercs);
        held.Add(other);

        Assert.True(held.Remove(mercs));
        Assert.False(held.Remove(mercs));
        Assert.Equal(1, held.Count);
        Assert.Equal(1, held.PendingOps);
        Assert.True(held.Contains("PerkTemplate", "perk.bribe", "womenace"));
    }

    [Fact]
    public void TakeUnreported_ReturnsEachBlockOnceAndKeepsItHeld()
    {
        var held = new HeldPatchBlocks((a, b) => a == b);
        held.Add(Block("PerkTemplate", "a", "m", 1, "X:1"));
        held.Add(Block("PerkTemplate", "b", "m", 1, "X:2"));

        Assert.Equal(2, held.TakeUnreported().Count);
        Assert.Empty(held.TakeUnreported());
        Assert.Equal(2, held.Count);

        held.Add(Block("PerkTemplate", "c", "m", 1, "X:3"));
        Assert.Equal("c", Assert.Single(held.TakeUnreported()).TemplateId);
    }

    [Fact]
    public void Missing_IsReplacedOnRetryAndKeyIsTheTemplate()
    {
        var block = Block("PerkTemplate", "perk.bribe", "mercs", 1, "EntityTemplate:a", "EntityTemplate:b");
        block.Missing = [Ref("EntityTemplate:b")];

        Assert.Equal(["EntityTemplate:b"], block.Missing.Select(m => m.ToString()));
        Assert.Equal(LateTemplateSet.Key("PerkTemplate", "perk.bribe"), block.TemplateKey);
    }

    [Fact]
    public void ResetReported_ReportsEveryHeldBlockAgain()
    {
        var held = new HeldPatchBlocks((a, b) => a == b);
        held.Add(Block("PerkTemplate", "a", "m", 1, "X:1"));
        Assert.Single(held.TakeUnreported());
        Assert.Empty(held.TakeUnreported());

        held.ResetReported();
        Assert.Single(held.TakeUnreported());
    }

    [Fact]
    public void AnyBlockWaitsOnOther_IgnoresOnlyTheNamedKey()
    {
        var held = new HeldPatchBlocks((a, b) => a == b);
        held.Add(Block("PerkTreeTemplate", "tree.a", "m", 1, "PerkTreeTemplate:tree.b"));
        Func<string, bool> names = type => type == "PerkTreeTemplate";

        // The one block waits on tree.b alone: from tree.b's point of view nothing else holds it.
        Assert.False(held.AnyBlockWaitsOnOther(names, "tree.a", key => key.ToString() == "PerkTreeTemplate:tree.b"));
        // From any other clone's point of view the block is a real hold.
        Assert.True(held.AnyBlockWaitsOnOther(names, "tree.a", key => key.ToString() == "PerkTreeTemplate:tree.c"));
        Assert.False(held.AnyBlockWaitsOnOther(names, "tree.z", key => key.ToString() == "PerkTreeTemplate:tree.c"));

        // A second mod's block waiting on something else holds tree.b back after all.
        held.Add(Block("PerkTreeTemplate", "tree.a", "other", 1, "SkillTemplate:s"));
        Assert.True(held.AnyBlockWaitsOnOther(names, "tree.a", key => key.ToString() == "PerkTreeTemplate:tree.b"));

        // Addressed under an ancestor name.
        Assert.True(held.ContainsTemplate(type => type is "BaseTreeTemplate" or "PerkTreeTemplate", "tree.a"));
        Assert.False(held.ContainsTemplate(type => type == "BaseTreeTemplate", "tree.a"));
    }
}
