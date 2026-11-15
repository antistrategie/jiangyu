using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Jiangyu.Loader.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Tests which array wrappers <see cref="Il2CppGcWrites"/> claims. Every
/// claimed wrapper has its element writes routed through the GC write
/// barrier; anything else keeps the reflective indexer path.
/// </summary>
public sealed class Il2CppGcWritesTests
{
    private sealed class DerivedReferenceArray : Il2CppReferenceArray<Il2CppObjectBase>
    {
        public DerivedReferenceArray(IntPtr pointer) : base(pointer) { }
    }

    [Theory]
    [InlineData(typeof(Il2CppStringArray))]
    [InlineData(typeof(Il2CppReferenceArray<Il2CppObjectBase>))]
    [InlineData(typeof(DerivedReferenceArray))]
    public void ReferenceElementWrappers_AreClaimed(Type arrayType)
    {
        Assert.True(Il2CppGcWrites.IsReferenceArrayWrapper(arrayType));
    }

    [Theory]
    [InlineData(typeof(Il2CppStructArray<int>))]
    [InlineData(typeof(string[]))]
    [InlineData(typeof(List<string>))]
    public void OtherCollections_AreNotClaimed(Type arrayType)
    {
        Assert.False(Il2CppGcWrites.IsReferenceArrayWrapper(arrayType));
    }
}
