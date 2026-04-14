using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Metadata;
using AssetRipper.Assets.Traversal;
using Jiangyu.Core.Assets;

namespace Jiangyu.Core.Tests.Assets;

public sealed class ObjectFieldInspectorTests
{
    [Fact]
    public void Inspect_PreservesDeterministicFieldOrdering()
    {
        var collection = new TestCollection("test.assets");
        var asset = new TestObject(collection, 100, "TestTemplate", (walker, self) =>
        {
            EmitPrimitiveField(walker, self, "zLast", 3);
            EmitPrimitiveField(walker, self, "alpha", 1);
            EmitPrimitiveField(walker, self, "middle", 2);
        });

        ObjectFieldInspection result = ObjectFieldInspector.Inspect(asset, maxDepth: 4, maxArraySampleLength: 8);

        Assert.Collection(
            result.Fields,
            field => Assert.Equal("zLast", field.Name),
            field => Assert.Equal("alpha", field.Name),
            field => Assert.Equal("middle", field.Name));
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Inspect_TruncatesNestedObjectsAtConfiguredDepth()
    {
        var collection = new TestCollection("test.assets");
        var asset = new TestObject(collection, 100, "TestTemplate", (walker, self) =>
        {
            EmitNestedObjectField(walker, self, "nested", nested =>
            {
                EmitPrimitiveField(walker, nested, "child", 7);
            });
        });

        ObjectFieldInspection result = ObjectFieldInspector.Inspect(asset, maxDepth: 1, maxArraySampleLength: 8);

        var nested = Assert.Single(result.Fields);
        Assert.Equal("object", nested.Kind);
        Assert.True(nested.Truncated);
        Assert.Null(nested.Fields);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Inspect_TruncatesArraysToConfiguredSampleLength()
    {
        var collection = new TestCollection("test.assets");
        var asset = new TestObject(collection, 100, "TestTemplate", (walker, self) =>
        {
            EmitArrayField(walker, self, "items", [1, 2, 3]);
        });

        ObjectFieldInspection result = ObjectFieldInspector.Inspect(asset, maxDepth: 4, maxArraySampleLength: 2);

        var array = Assert.Single(result.Fields);
        Assert.Equal("array", array.Kind);
        Assert.Equal(3, array.Count);
        Assert.True(array.Truncated);
        Assert.NotNull(array.Elements);
        Assert.Equal(2, array.Elements!.Count);
        Assert.Equal([1, 2], array.Elements.Select(element => Convert.ToInt32(element.Value)).ToArray());
        Assert.True(result.Truncated);
    }

    private static void EmitPrimitiveField<T>(AssetWalker walker, IUnityAssetBase owner, string name, T value)
        where T : notnull
    {
        if (walker.EnterField(owner, name))
        {
            walker.VisitPrimitive(value);
            walker.ExitField(owner, name);
        }
    }

    private static void EmitArrayField<T>(AssetWalker walker, IUnityAssetBase owner, string name, IReadOnlyList<T> values)
        where T : notnull
    {
        if (!walker.EnterField(owner, name))
        {
            return;
        }

        if (walker.EnterList(values))
        {
            for (int i = 0; i < values.Count; i++)
            {
                walker.VisitPrimitive(values[i]);
                if (i < values.Count - 1)
                {
                    walker.DivideList(values);
                }
            }

            walker.ExitList(values);
        }

        walker.ExitField(owner, name);
    }

    private static void EmitNestedObjectField(AssetWalker walker, IUnityAssetBase owner, string name, Action<TestNestedAsset> emit)
    {
        if (!walker.EnterField(owner, name))
        {
            return;
        }

        var nested = new TestNestedAsset(emit);
        nested.WalkStandard(walker);
        walker.ExitField(owner, name);
    }

    private sealed class TestNestedAsset(Action<TestNestedAsset> emit) : UnityAssetBase
    {
        private readonly Action<TestNestedAsset> _emit = emit;

        public override void WalkStandard(AssetWalker walker)
        {
            if (walker.EnterAsset(this))
            {
                _emit(this);
                walker.ExitAsset(this);
            }
        }
    }

    private sealed class TestObject : NullObject
    {
        private readonly Action<AssetWalker, TestObject> _emit;
        private readonly string _className;

        public TestObject(TestCollection collection, long pathId, string className, Action<AssetWalker, TestObject> emit)
            : base(new AssetInfo(collection, pathId, 114))
        {
            _emit = emit;
            _className = className;
            collection.Register(this);
        }

        public override string ClassName => _className;

        public override void WalkStandard(AssetWalker walker)
        {
            if (walker.EnterAsset(this))
            {
                _emit(walker, this);
                walker.ExitAsset(this);
            }
        }
    }

    private sealed class TestBundle : Bundle
    {
        public override string Name => "TestBundle";
    }

    private sealed class TestCollection : AssetCollection
    {
        public TestCollection(string name) : base(new TestBundle())
        {
            Name = name;
        }

        public void Register(IUnityObjectBase asset) => AddAsset(asset);
    }
}
