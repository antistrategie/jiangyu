using Jiangyu.Core.Templates;
using Jiangyu.Shared.Replacements;
using Xunit;

namespace Jiangyu.Core.Tests.Templates;

public sealed class AssetAdditionsCatalogTests : IDisposable
{
    private readonly string _tempRoot;

    public AssetAdditionsCatalogTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"jiangyu-additions-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void AdditionRoot_FlatLayout_Indexed()
    {
        var spritesDir = Path.Combine(_tempRoot, "additions", AssetCategory.Sprites);
        Directory.CreateDirectory(spritesDir);
        File.WriteAllText(Path.Combine(spritesDir, "icon.png"), "");

        var catalog = new FileSystemAssetAdditionsCatalog(Path.Combine(_tempRoot, "additions"));

        Assert.True(catalog.Contains(AssetCategory.Sprites, "icon"));
        Assert.False(catalog.Contains(AssetCategory.Sprites, "missing"));
    }

    [Fact]
    public void AdditionRoot_NestedLayout_IndexedWithSlashedStem()
    {
        var spritesDir = Path.Combine(_tempRoot, "additions", AssetCategory.Sprites, "lrm5");
        Directory.CreateDirectory(spritesDir);
        File.WriteAllText(Path.Combine(spritesDir, "icon.png"), "");

        var catalog = new FileSystemAssetAdditionsCatalog(Path.Combine(_tempRoot, "additions"));

        Assert.True(catalog.Contains(AssetCategory.Sprites, "lrm5/icon"));
        // Stem-only form without the directory does not match.
        Assert.False(catalog.Contains(AssetCategory.Sprites, "icon"));
    }

    [Fact]
    public void UnityPrefabsDir_FlatLayout_IndexedUnderPrefabsCategory()
    {
        var unityPrefabsDir = Path.Combine(_tempRoot, "unity", "Assets", "Prefabs");
        Directory.CreateDirectory(unityPrefabsDir);
        File.WriteAllText(Path.Combine(unityPrefabsDir, "test_cube.prefab"), "");

        var catalog = new FileSystemAssetAdditionsCatalog(
            additionRoot: Path.Combine(_tempRoot, "additions"),
            unityPrefabsDir: unityPrefabsDir);

        Assert.True(catalog.Contains(AssetCategory.Prefabs, "test_cube"));
    }

    [Fact]
    public void UnityPrefabsDir_NestedLayout_IndexedWithSlashedStem()
    {
        var unityPrefabsDir = Path.Combine(_tempRoot, "unity", "Assets", "Prefabs");
        var nested = Path.Combine(unityPrefabsDir, "dir", "sub");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "test_cube.prefab"), "");

        var catalog = new FileSystemAssetAdditionsCatalog(
            additionRoot: Path.Combine(_tempRoot, "additions"),
            unityPrefabsDir: unityPrefabsDir);

        Assert.True(catalog.Contains(AssetCategory.Prefabs, "dir/sub/test_cube"));
        Assert.False(catalog.Contains(AssetCategory.Prefabs, "test_cube"));
    }

    [Fact]
    public void UnityPrefabsDir_AndAdditionRoot_Merge()
    {
        var prefabsCategoryDir = Path.Combine(_tempRoot, "additions", AssetCategory.Prefabs);
        Directory.CreateDirectory(prefabsCategoryDir);
        File.WriteAllText(Path.Combine(prefabsCategoryDir, "from_escape_hatch.bundle"), "");

        var unityPrefabsDir = Path.Combine(_tempRoot, "unity", "Assets", "Prefabs");
        Directory.CreateDirectory(unityPrefabsDir);
        File.WriteAllText(Path.Combine(unityPrefabsDir, "from_unity.prefab"), "");

        var catalog = new FileSystemAssetAdditionsCatalog(
            additionRoot: Path.Combine(_tempRoot, "additions"),
            unityPrefabsDir: unityPrefabsDir);

        Assert.True(catalog.Contains(AssetCategory.Prefabs, "from_escape_hatch"));
        Assert.True(catalog.Contains(AssetCategory.Prefabs, "from_unity"));
    }

    [Fact]
    public void UnityPrefabsDir_NameCollision_WithAdditionRoot_RecordedAsConflict()
    {
        var prefabsCategoryDir = Path.Combine(_tempRoot, "additions", AssetCategory.Prefabs);
        Directory.CreateDirectory(prefabsCategoryDir);
        File.WriteAllText(Path.Combine(prefabsCategoryDir, "same_name.bundle"), "");

        var unityPrefabsDir = Path.Combine(_tempRoot, "unity", "Assets", "Prefabs");
        Directory.CreateDirectory(unityPrefabsDir);
        File.WriteAllText(Path.Combine(unityPrefabsDir, "same_name.prefab"), "");

        var catalog = new FileSystemAssetAdditionsCatalog(
            additionRoot: Path.Combine(_tempRoot, "additions"),
            unityPrefabsDir: unityPrefabsDir);

        Assert.Contains($"{AssetCategory.Prefabs}/same_name", catalog.ConflictingNames);
    }

    [Fact]
    public void MissingDirs_DoesNotThrow()
    {
        var catalog = new FileSystemAssetAdditionsCatalog(
            additionRoot: Path.Combine(_tempRoot, "does-not-exist-additions"),
            unityPrefabsDir: Path.Combine(_tempRoot, "does-not-exist-unity"));

        Assert.False(catalog.Contains(AssetCategory.Prefabs, "anything"));
        Assert.Empty(catalog.ConflictingNames);
    }

    [Fact]
    public void NullUnityPrefabsDir_AdditionRootStillIndexed()
    {
        var spritesDir = Path.Combine(_tempRoot, "additions", AssetCategory.Sprites);
        Directory.CreateDirectory(spritesDir);
        File.WriteAllText(Path.Combine(spritesDir, "icon.png"), "");

        var catalog = new FileSystemAssetAdditionsCatalog(
            additionRoot: Path.Combine(_tempRoot, "additions"),
            unityPrefabsDir: null);

        Assert.True(catalog.Contains(AssetCategory.Sprites, "icon"));
    }
}
