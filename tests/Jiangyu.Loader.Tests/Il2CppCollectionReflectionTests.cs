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
}
