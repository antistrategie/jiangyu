using Jiangyu.Core.Templates;

namespace Jiangyu.Core.Tests.Templates;

public class HashableIdFieldRegistryTests
{
    [Fact]
    public void Fnv1a32_IsDeterministic()
    {
        var a = HashableIdFieldRegistry.Fnv1a32("custom_rifle_fire");
        var b = HashableIdFieldRegistry.Fnv1a32("custom_rifle_fire");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Fnv1a32_DistinctStringsProduceDistinctHashes()
    {
        var a = HashableIdFieldRegistry.Fnv1a32("foo");
        var b = HashableIdFieldRegistry.Fnv1a32("bar");
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("Stem.Sound", "id")]
    [InlineData("Il2CppStem.Sound", "id")]
    [InlineData("Stem.ID", "itemId")]
    [InlineData("Il2CppStem.ID", "itemId")]
    [InlineData("Stem.ID", "bankId")]
    [InlineData("Il2CppStem.ID", "bankId")]
    public void IsHashable_RegisteredFields(string typeFqn, string field)
    {
        Assert.True(HashableIdFieldRegistry.IsHashable(typeFqn, field));
    }

    [Theory]
    [InlineData("Stem.Sound", "name")]
    [InlineData("Stem.Sound", "")]
    [InlineData("", "id")]
    [InlineData("Stem.Unknown", "id")]
    public void IsHashable_UnregisteredCombosReturnFalse(string typeFqn, string field)
    {
        Assert.False(HashableIdFieldRegistry.IsHashable(typeFqn, field));
    }

    [Fact]
    public void TryResolve_Fnv1aPath_ReturnsHash()
    {
        var ok = HashableIdFieldRegistry.TryResolve(
            "Stem.Sound", "id", "custom_rifle_fire",
            out var resolved, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(HashableIdFieldRegistry.Fnv1a32("custom_rifle_fire"), resolved);
    }

    [Fact]
    public void TryResolve_BankNamePath_KnownBank()
    {
        var ok = HashableIdFieldRegistry.TryResolve(
            "Stem.ID", "bankId", "weapons_soundbank",
            out var resolved, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(7, resolved);
    }

    [Fact]
    public void TryResolve_BankNamePath_UnknownBank()
    {
        var ok = HashableIdFieldRegistry.TryResolve(
            "Stem.ID", "bankId", "not_a_real_bank",
            out var _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("not_a_real_bank", error);
        // Error should list known banks so the modder can correct.
        Assert.Contains("weapons_soundbank", error);
    }

    [Fact]
    public void TryResolve_UnregisteredField_Errors()
    {
        var ok = HashableIdFieldRegistry.TryResolve(
            "Stem.Sound", "name", "anything",
            out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("not registered", error);
    }

    [Fact]
    public void TryResolve_EmptyTypeOrField_Errors()
    {
        Assert.False(HashableIdFieldRegistry.TryResolve("", "id", "x", out _, out _));
        Assert.False(HashableIdFieldRegistry.TryResolve("Stem.Sound", "", "x", out _, out _));
    }

    [Fact]
    public void TryResolve_NullValue_Errors()
    {
        var ok = HashableIdFieldRegistry.TryResolve(
            "Stem.Sound", "id", null!,
            out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void Fnv1a32_CrossFieldReferencesShareHashSpace()
    {
        // The whole point of the registry: a string written into Sound.id
        // and the same string written into a consumer's ID.itemId both
        // resolve to the same integer, so cross-field references stay
        // linked without the modder writing the raw number.
        const string name = "custom_rifle_fire";

        HashableIdFieldRegistry.TryResolve("Stem.Sound", "id", name, out var soundId, out _);
        HashableIdFieldRegistry.TryResolve("Stem.ID", "itemId", name, out var itemId, out _);

        Assert.Equal(soundId, itemId);
    }
}
