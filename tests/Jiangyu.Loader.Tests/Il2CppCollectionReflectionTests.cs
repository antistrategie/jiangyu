using Jiangyu.Loader.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Tests <see cref="Il2CppCollectionReflection.TryRebuildReferenceArrayBatch"/>
/// against a synthetic array wrapper that mirrors the shape the helper
/// expects of <c>Il2CppReferenceArray&lt;T&gt;</c>: a <c>Length</c>
/// property, an integer indexer, and a constructor taking a managed
/// <c>T[]</c>. The helper drives the source by reflection over these
/// members, so the BCL-only fixture exercises the same code paths the
/// live IL2CPP wrapper does in production.
/// </summary>
public sealed class Il2CppCollectionReflectionTests
{
    private sealed class FakeRefArray<T>
    {
        private readonly T[] _items;

        public FakeRefArray(T[] items)
        {
            _items = items ?? throw new System.ArgumentNullException(nameof(items));
        }

        public int Length => _items.Length;

        public T this[int i]
        {
            get => _items[i];
            set => _items[i] = value;
        }

        public T[] Snapshot() => (T[])_items.Clone();
    }

    // Count rather than Length, the shape Il2CppSystem.Collections.Generic.List<T> presents.
    private sealed class FakeList<T>
    {
        private readonly System.Collections.Generic.List<T> _items;
        public FakeList(params T[] items) => _items = new System.Collections.Generic.List<T>(items);
        public int Count => _items.Count;
        public T this[int i] { get => _items[i]; set => _items[i] = value; }
    }

    // Neither Length/Count nor an indexer: the shape the helper has to refuse rather than guess at.
    private sealed class Opaque
    {
    }

    private sealed class Element
    {
        public string Name;
        public Element(string name) => Name = name;
    }

    [Fact]
    public void Batch_AppendsAllElementsInOrder_AfterExistingEntries()
    {
        var a = new Element("a");
        var b = new Element("b");
        var c = new Element("c");
        var d = new Element("d");
        var source = new FakeRefArray<Element>(new[] { a, b });

        var ok = Il2CppCollectionReflection.TryRebuildReferenceArrayBatch(
            source,
            typeof(FakeRefArray<Element>),
            typeof(Element),
            new object[] { c, d },
            out var fresh,
            out var error);

        Assert.True(ok, error);
        var freshArray = Assert.IsType<FakeRefArray<Element>>(fresh);
        Assert.Equal(4, freshArray.Length);
        Assert.Same(a, freshArray[0]);
        Assert.Same(b, freshArray[1]);
        Assert.Same(c, freshArray[2]);
        Assert.Same(d, freshArray[3]);
    }

    [Fact]
    public void Batch_PreservesSourceReferenceIdentity()
    {
        // The clone applier needs the source's element refs preserved
        // verbatim; the helper must not deep-copy. If this ever regresses,
        // the per-trigger bucket dictionary would point at fresh
        // wrappers while the master array points at the originals.
        var existing = new Element("existing");
        var source = new FakeRefArray<Element>(new[] { existing });

        var ok = Il2CppCollectionReflection.TryRebuildReferenceArrayBatch(
            source,
            typeof(FakeRefArray<Element>),
            typeof(Element),
            new object[] { new Element("new") },
            out var fresh,
            out _);

        Assert.True(ok);
        var freshArray = (FakeRefArray<Element>)fresh;
        Assert.Same(existing, freshArray[0]);
    }

    [Fact]
    public void Batch_SourceUnchanged()
    {
        var a = new Element("a");
        var b = new Element("b");
        var source = new FakeRefArray<Element>(new[] { a, b });
        var sourceSnapshot = source.Snapshot();

        Il2CppCollectionReflection.TryRebuildReferenceArrayBatch(
            source,
            typeof(FakeRefArray<Element>),
            typeof(Element),
            new object[] { new Element("c") },
            out _,
            out _);

        Assert.Equal(sourceSnapshot.Length, source.Length);
        for (var i = 0; i < sourceSnapshot.Length; i++)
            Assert.Same(sourceSnapshot[i], source[i]);
    }

    [Fact]
    public void Batch_EmptyAppendList_FallsBackToCopyOnly()
    {
        var a = new Element("a");
        var source = new FakeRefArray<Element>(new[] { a });

        var ok = Il2CppCollectionReflection.TryRebuildReferenceArrayBatch(
            source,
            typeof(FakeRefArray<Element>),
            typeof(Element),
            System.Array.Empty<object>(),
            out var fresh,
            out var error);

        Assert.True(ok, error);
        var freshArray = (FakeRefArray<Element>)fresh;
        Assert.Equal(1, freshArray.Length);
        Assert.Same(a, freshArray[0]);
        Assert.NotSame(source, fresh);
    }

    [Fact]
    public void Batch_NullAppendList_FallsBackToCopyOnly()
    {
        var source = new FakeRefArray<Element>(new[] { new Element("a") });

        var ok = Il2CppCollectionReflection.TryRebuildReferenceArrayBatch(
            source,
            typeof(FakeRefArray<Element>),
            typeof(Element),
            appendedElements: null,
            out var fresh,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(1, ((FakeRefArray<Element>)fresh).Length);
    }

    [Fact]
    public void Batch_NullSource_ReportsError()
    {
        var ok = Il2CppCollectionReflection.TryRebuildReferenceArrayBatch(
            source: null,
            typeof(FakeRefArray<Element>),
            typeof(Element),
            new object[] { new Element("x") },
            out var fresh,
            out var error);

        Assert.False(ok);
        Assert.Null(fresh);
        Assert.Contains("source array is null", error);
    }

    [Fact]
    public void Batch_EmptySource_AppendsAllNewElements()
    {
        var source = new FakeRefArray<Element>(System.Array.Empty<Element>());
        var x = new Element("x");
        var y = new Element("y");

        var ok = Il2CppCollectionReflection.TryRebuildReferenceArrayBatch(
            source,
            typeof(FakeRefArray<Element>),
            typeof(Element),
            new object[] { x, y },
            out var fresh,
            out var error);

        Assert.True(ok, error);
        var freshArray = (FakeRefArray<Element>)fresh;
        Assert.Equal(2, freshArray.Length);
        Assert.Same(x, freshArray[0]);
        Assert.Same(y, freshArray[1]);
    }

    // The SoundBank fixup grows busIndices to sounds.Count. Extending must
    // carry every existing bus assignment over: a dropped entry silently
    // re-routes that sound to bus 0.
    [Fact]
    public void Resize_Extend_KeepsExistingEntriesAndDefaultsTheRest()
    {
        var source = new FakeRefArray<int>(new[] { 3, 1, 4 });

        var ok = Il2CppCollectionReflection.TryResizeArray(source, 5, out var fresh, out var error);

        Assert.True(ok, error);
        var freshArray = Assert.IsType<FakeRefArray<int>>(fresh);
        Assert.Equal(5, freshArray.Length);
        Assert.Equal(3, freshArray[0]);
        Assert.Equal(1, freshArray[1]);
        Assert.Equal(4, freshArray[2]);
        Assert.Equal(0, freshArray[3]);
        Assert.Equal(0, freshArray[4]);
    }

    [Fact]
    public void Resize_Shrink_TruncatesWithoutThrowing()
    {
        var source = new FakeRefArray<int>(new[] { 3, 1, 4, 1, 5 });

        var ok = Il2CppCollectionReflection.TryResizeArray(source, 2, out var fresh, out var error);

        Assert.True(ok, error);
        var freshArray = (FakeRefArray<int>)fresh;
        Assert.Equal(2, freshArray.Length);
        Assert.Equal(3, freshArray[0]);
        Assert.Equal(1, freshArray[1]);
    }

    [Fact]
    public void Resize_SameLength_CopiesEveryEntry()
    {
        var source = new FakeRefArray<int>(new[] { 7, 8 });

        var ok = Il2CppCollectionReflection.TryResizeArray(source, 2, out var fresh, out var error);

        Assert.True(ok, error);
        var freshArray = (FakeRefArray<int>)fresh;
        Assert.NotSame(source, fresh);
        Assert.Equal(7, freshArray[0]);
        Assert.Equal(8, freshArray[1]);
    }

    [Fact]
    public void Resize_SourceUnchanged()
    {
        var source = new FakeRefArray<int>(new[] { 3, 1, 4 });
        var snapshot = source.Snapshot();

        Il2CppCollectionReflection.TryResizeArray(source, 6, out _, out _);

        Assert.Equal(snapshot.Length, source.Length);
        for (var i = 0; i < snapshot.Length; i++)
            Assert.Equal(snapshot[i], source[i]);
    }

    [Fact]
    public void Resize_PlainManagedArray_ResizesInKind()
    {
        var ok = Il2CppCollectionReflection.TryResizeArray(new[] { 3, 1 }, 4, out var fresh, out var error);

        Assert.True(ok, error);
        Assert.Equal(new[] { 3, 1, 0, 0 }, Assert.IsType<int[]>(fresh));
    }

    [Fact]
    public void Resize_PreservesReferenceIdentity()
    {
        var a = new Element("a");
        var source = new FakeRefArray<Element>(new[] { a });

        var ok = Il2CppCollectionReflection.TryResizeArray(source, 3, out var fresh, out var error);

        Assert.True(ok, error);
        var freshArray = (FakeRefArray<Element>)fresh;
        Assert.Same(a, freshArray[0]);
        Assert.Null(freshArray[1]);
    }

    [Fact]
    public void Resize_NullSource_ReportsError()
    {
        var ok = Il2CppCollectionReflection.TryResizeArray(null, 3, out var fresh, out var error);

        Assert.False(ok);
        Assert.Null(fresh);
        Assert.Contains("source array is null", error);
    }

    [Fact]
    public void Resize_NegativeLength_ReportsError()
    {
        var ok = Il2CppCollectionReflection.TryResizeArray(
            new FakeRefArray<int>(new[] { 1 }), -1, out var fresh, out var error);

        Assert.False(ok);
        Assert.Null(fresh);
        Assert.Contains("negative", error);
    }

    // A shape with no managed-array ctor must report rather than resize, so
    // the SoundBank fixup leaves busIndices alone instead of blanking it.
    [Fact]
    public void Resize_NonArrayShape_ReportsErrorAndResizesNothing()
    {
        var ok = Il2CppCollectionReflection.TryResizeArray(
            new System.Collections.Generic.List<int> { 1, 2 }, 4, out var fresh, out var error);

        Assert.False(ok);
        Assert.Null(fresh);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ReadElements_ReadsAnArrayShapeInOrder()
    {
        var a = new Element("a");
        var b = new Element("b");
        var source = new FakeRefArray<Element>(new[] { a, b });

        Assert.True(Il2CppCollectionReflection.TryReadElements(source, out var elements, out var error), error);
        Assert.Equal(new object[] { a, b }, elements);
    }

    [Fact]
    public void ReadElements_ReadsAListShapeThroughCount()
    {
        // The conversation registry checks a per-trigger bucket, which is a List, and a master
        // array, which is not. One helper has to read both.
        var a = new Element("a");
        var b = new Element("b");
        var source = new FakeList<Element>(a, b);

        Assert.True(Il2CppCollectionReflection.TryReadElements(source, out var elements, out var error), error);
        Assert.Equal(new object[] { a, b }, elements);
    }

    [Fact]
    public void ReadElements_EmptyCollectionSucceedsWithNoElements()
    {
        var source = new FakeRefArray<Element>(System.Array.Empty<Element>());

        Assert.True(Il2CppCollectionReflection.TryReadElements(source, out var elements, out var error), error);
        Assert.Empty(elements);
    }

    [Fact]
    public void ReadElements_RefusesAShapeItCannotWalk()
    {
        // Must fail rather than report an empty collection: a caller deciding membership from an
        // empty answer would conclude nothing is present and append a duplicate of everything.
        Assert.False(Il2CppCollectionReflection.TryReadElements(new Opaque(), out var elements, out var error));
        Assert.Null(elements);
        Assert.Contains("Length/Count", error);
    }

    [Fact]
    public void ReadElements_NullCollectionFails()
    {
        Assert.False(Il2CppCollectionReflection.TryReadElements(null, out var elements, out var error));
        Assert.Null(elements);
        Assert.Contains("null", error);
    }
}
