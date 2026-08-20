using Jiangyu.Loader.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Covers the field-by-field readback comparison the applier falls back to for
/// IL2CPP value types (<see cref="TemplatePatchApplier.GeneratedPropertiesMatch"/>).
/// A value type is projected as a class over a boxed native instance, so a
/// collection Add copies the value into the collection's storage and the
/// readback indexer boxes a fresh copy at a new address. Comparing the two
/// wrappers by pointer reports a failed write on every append, which is what
/// this comparison replaces.
///
/// Tested against plain managed fixtures: the property walk is pure reflection,
/// so it runs without a live IL2CPP game. The value-type detection that gates
/// it asks the native runtime and is verified in-game.
/// </summary>
public sealed class ReadbackVerificationTests
{
    private sealed class Material
    {
        public string Template { get; set; } = "";
        public int Count { get; set; }
    }

    private sealed class Opaque
    {
        public int Hidden { private get; set; }

        public int Read() => Hidden;
    }

    private sealed class Throwing
    {
        public int Explodes => throw new InvalidOperationException("no such field");

        public int Count { get; set; }
    }

    [Fact]
    public void GeneratedPropertiesMatch_TwoCopiesOfTheSameValueMatch()
    {
        var written = new Material { Template = "item.ammo", Count = 4 };
        var readback = new Material { Template = "item.ammo", Count = 4 };

        // Distinct instances, so reference identity says no and the fields say yes.
        Assert.NotSame(written, readback);
        Assert.True(TemplatePatchApplier.GeneratedPropertiesMatch(written, readback));
    }

    [Fact]
    public void GeneratedPropertiesMatch_DifferingFieldFailsTheComparison()
    {
        var written = new Material { Template = "item.ammo", Count = 4 };

        Assert.False(TemplatePatchApplier.GeneratedPropertiesMatch(
            written, new Material { Template = "item.ammo", Count = 5 }));
        Assert.False(TemplatePatchApplier.GeneratedPropertiesMatch(
            written, new Material { Template = "item.fuel", Count = 4 }));
    }

    [Fact]
    public void GeneratedPropertiesMatch_TypeWithNoReadableFieldReportsAMatch()
    {
        var written = new Opaque { Hidden = 1 };
        var readback = new Opaque { Hidden = 2 };

        // Nothing to read means nothing to verify, and a diagnostic that cannot
        // see the field stays quiet rather than warning on every write.
        Assert.True(TemplatePatchApplier.GeneratedPropertiesMatch(written, readback));
        Assert.NotEqual(written.Read(), readback.Read());
    }

    [Fact]
    public void GeneratedPropertiesMatch_ThrowingGetterLeavesTheRestOfTheWalkIntact()
    {
        var written = new Throwing { Count = 4 };

        Assert.True(TemplatePatchApplier.GeneratedPropertiesMatch(written, new Throwing { Count = 4 }));
        Assert.False(TemplatePatchApplier.GeneratedPropertiesMatch(written, new Throwing { Count = 5 }));
    }
}
