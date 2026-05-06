using Jiangyu.Shared.Replacements;
using Xunit;

namespace Jiangyu.Loader.Tests;

public sealed class AssetCategoryTests
{
    [Theory]
    [InlineData("Sprite", AssetCategory.Sprites)]
    [InlineData("Texture2D", AssetCategory.Textures)]
    [InlineData("AudioClip", AssetCategory.Audio)]
    [InlineData("Material", AssetCategory.Materials)]
    public void ForClassName_SupportedKinds_ReturnFolderName(string className, string expected)
    {
        Assert.Equal(expected, AssetCategory.ForClassName(className));
    }

    [Theory]
    [InlineData("Sprite")]
    [InlineData("Texture2D")]
    [InlineData("AudioClip")]
    [InlineData("Material")]
    public void IsSupported_TrueForKindsAppliersResolveToday(string className)
    {
        Assert.True(AssetCategory.IsSupported(className));
    }

    [Fact]
    public void ForClassName_Mesh_ThrowsDeferralWithPointerToTodo()
    {
        // Mesh and GameObject have folder constants reserved (so producers
        // and the prefab-construction layer agree on the path), but the
        // dispatcher refuses them today. The thrown message points the
        // future implementer at PREFAB_CLONING_TODO so the deferral is
        // discoverable from a stack trace alone.
        var ex = Assert.Throws<System.NotSupportedException>(
            () => AssetCategory.ForClassName("Mesh"));
        Assert.Contains("PREFAB_CLONING_TODO", ex.Message);
    }

    [Fact]
    public void ForClassName_GameObject_ThrowsDeferralWithPointerToTodo()
    {
        var ex = Assert.Throws<System.NotSupportedException>(
            () => AssetCategory.ForClassName("GameObject"));
        Assert.Contains("PREFAB_CLONING_TODO", ex.Message);
    }

    [Fact]
    public void IsSupported_DeferredKinds_ReturnFalse()
    {
        // IsSupported is the safe pre-check before ForClassName; deferred
        // kinds report unsupported rather than throwing, so the validator
        // and applier can produce a clean modder-facing error instead of
        // surfacing the exception.
        Assert.False(AssetCategory.IsSupported("Mesh"));
        Assert.False(AssetCategory.IsSupported("GameObject"));
    }

    [Fact]
    public void ForClassName_UnknownType_ReturnsNull()
    {
        Assert.Null(AssetCategory.ForClassName("ScriptableObject"));
        Assert.Null(AssetCategory.ForClassName(""));
    }

    [Theory]
    [InlineData("lrm5/icon", "lrm5__icon")]
    [InlineData("lrm5/icon_equipment", "lrm5__icon_equipment")]
    [InlineData("a/b/c/leaf", "a__b__c__leaf")]
    [InlineData("flat", "flat")]
    [InlineData("with_underscore", "with_underscore")]
    public void ToBundleAssetName_ForwardSlashes_FlattenedToDoubleUnderscore(string logical, string expected)
    {
        Assert.Equal(expected, AssetCategory.ToBundleAssetName(logical));
    }

    [Fact]
    public void ToBundleAssetName_BackslashesAlsoFlattened()
    {
        // Defensive: native Windows path separators aren't valid in KDL
        // (the parser rejects them), but Path.GetRelativePath on Windows
        // can produce them when the compiler walks the additions tree.
        // The translation must produce the same key as the forward-slash
        // form so cross-platform builds match the runtime lookup.
        Assert.Equal("a__b__c", AssetCategory.ToBundleAssetName("a\\b\\c"));
    }

    [Fact]
    public void ToBundleAssetName_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, AssetCategory.ToBundleAssetName(""));
    }

    [Fact]
    public void LogicalAdditionName_StripsExtensionAndPreservesSlashes()
    {
        // The logical name is what the modder writes in KDL
        // (`asset="lrm5/icon"`); the compile pipeline and the studio picker
        // both build it from a filesystem path so they have to agree on
        // the same shape.
        var root = System.IO.Path.Combine("any", "assets", "additions", "sprites");
        var file = System.IO.Path.Combine(root, "lrm5", "icon.png");
        Assert.Equal("lrm5/icon", AssetCategory.LogicalAdditionName(root, file));
    }

    [Fact]
    public void LogicalAdditionName_HandlesFilesAtCategoryRoot()
    {
        var root = System.IO.Path.Combine("p", "assets", "additions", "sprites");
        var file = System.IO.Path.Combine(root, "icon.png");
        Assert.Equal("icon", AssetCategory.LogicalAdditionName(root, file));
    }

    [Fact]
    public void LogicalAdditionName_StripsMultiDotExtensionOnce()
    {
        // Path.GetExtension returns only the trailing extension, so a name
        // like "icon.tex.png" keeps the inner ".tex" segment; the modder
        // referencing this asset in KDL writes "icon.tex".
        var root = System.IO.Path.Combine("p", "assets", "additions", "sprites");
        var file = System.IO.Path.Combine(root, "icon.tex.png");
        Assert.Equal("icon.tex", AssetCategory.LogicalAdditionName(root, file));
    }

    [Theory]
    [InlineData(AssetCategory.Sprites, new[] { ".png", ".jpg", ".jpeg" })]
    [InlineData(AssetCategory.Textures, new[] { ".png", ".jpg", ".jpeg" })]
    [InlineData(AssetCategory.Audio, new[] { ".wav", ".ogg", ".mp3" })]
    public void AdditionExtensionsForCategory_KnownCategories(string category, string[] expected)
    {
        Assert.Equal(expected, AssetCategory.AdditionExtensionsForCategory(category));
    }

    [Theory]
    [InlineData(AssetCategory.Materials)]
    [InlineData(AssetCategory.Meshes)]
    [InlineData(AssetCategory.Prefabs)]
    [InlineData("unknown")]
    public void AdditionExtensionsForCategory_UnsupportedReturnsEmpty(string category)
    {
        // Categories without an addition pipeline (Materials hasn't grown a
        // bundle dictionary, Meshes/Prefabs wait on prefab construction)
        // return empty so the studio picker offers nothing rather than
        // suggesting files the build will silently drop.
        Assert.Empty(AssetCategory.AdditionExtensionsForCategory(category));
    }
}
