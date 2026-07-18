using Jiangyu.Loader.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Covers the write-back primitive the in-place struct-element edit relies on:
/// <see cref="TemplatePatchApplier.TryBindArrayElement"/> reads an element copy
/// and stores a mutated copy back through the collection indexer. The runtime
/// visitor's descent hands back a boxed copy of a value-type element (structs
/// copy on read), mutates it via inner set ops, then the OnCompleted chain
/// calls this binder to persist it (collection[index] = mutatedCopy).
///
/// Tested against plain managed <c>List&lt;T&gt;</c> / <c>T[]</c> fixtures: the
/// binder's indexer and managed-array branches are pure reflection, so the
/// mechanism runs without a live IL2CPP game. The Il2CppStructArray marshalling
/// of the same shape is verified in-game.
/// </summary>
public sealed class StructElementWriteBackTests
{
    private struct WbVec
    {
        public int X { get; set; }
        public int Y { get; set; }
    }

    [Fact]
    public void TryBindArrayElement_WritesMutatedStructCopyBackIntoList()
    {
        var list = new List<WbVec> { new() { X = 1, Y = 2 } };

        var bound = TemplatePatchApplier.TryBindArrayElement(
            list, 0, out _, out var setter, out var getter, out var error);

        Assert.True(bound, error);

        // Descent reads a copy; mutate it, then write it back.
        var copy = (WbVec)getter();
        copy.X = 42;
        setter(copy);

        Assert.Equal(42, list[0].X);
        // Sibling field on the element is preserved by the whole-element write.
        Assert.Equal(2, list[0].Y);
    }

    [Fact]
    public void TryBindArrayElement_WritesMutatedStructCopyBackIntoArray()
    {
        var array = new[] { new WbVec { X = 1, Y = 2 } };

        var bound = TemplatePatchApplier.TryBindArrayElement(
            array, 0, out _, out var setter, out var getter, out var error);

        Assert.True(bound, error);

        var copy = (WbVec)getter();
        copy.Y = 99;
        setter(copy);

        Assert.Equal(99, array[0].Y);
        Assert.Equal(1, array[0].X);
    }

    [Fact]
    public void TryBindArrayElement_ElementReadIsACopy_MutatingWithoutWriteBackDoesNotPersist()
    {
        // Confirms why the write-back is necessary: mutating the value the
        // getter returns, without calling the setter, leaves the collection
        // untouched. This is exactly the trap in-place struct descent avoids.
        var list = new List<WbVec> { new() { X = 1, Y = 2 } };

        TemplatePatchApplier.TryBindArrayElement(
            list, 0, out _, out _, out var getter, out _);

        var copy = (WbVec)getter();
        copy.X = 7;

        Assert.Equal(1, list[0].X);
    }
}
