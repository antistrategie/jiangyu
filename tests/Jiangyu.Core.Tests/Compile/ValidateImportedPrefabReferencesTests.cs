using Jiangyu.Core.Compile;
using Jiangyu.Core.Models;
using Xunit;

namespace Jiangyu.Core.Tests.Compile;

public sealed class ValidateImportedPrefabReferencesTests : IDisposable
{
    private readonly string _projectDir;
    private readonly string _assetsDir;
    private readonly string _importedDir;

    public ValidateImportedPrefabReferencesTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), $"jiangyu-importvalid-{Guid.NewGuid():N}");
        _assetsDir = Path.Combine(_projectDir, "unity", "Assets");
        _importedDir = Path.Combine(_assetsDir, "Imported");
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectDir))
            Directory.Delete(_projectDir, recursive: true);
    }

    [Fact]
    public void NoUnityDir_ReturnsNull()
    {
        var manifest = NewManifest(imports: null);

        var result = ImportedPrefabValidator.Validate(_projectDir, manifest);

        Assert.Null(result);
    }

    [Fact]
    public void AssetsDirExists_ButNoImportedDir_ReturnsNull()
    {
        Directory.CreateDirectory(_assetsDir);
        WriteFile(Path.Combine(_assetsDir, "Authored", "char.prefab"),
            "m_Script: {fileID: 1, guid: deadbeefdeadbeefdeadbeefdeadbeef, type: 3}");
        var manifest = NewManifest(imports: null);

        var result = ImportedPrefabValidator.Validate(_projectDir, manifest);

        Assert.Null(result);
    }

    [Fact]
    public void ImportedDeclared_AndReferenced_ReturnsNull()
    {
        var guid = NewGuid();
        SeedImported("rmc_default_female_soldier_2", guid);
        WriteFile(Path.Combine(_assetsDir, "Prefabs", "my_char.prefab"),
            $"m_Script: {{fileID: 1, guid: {guid}, type: 3}}");

        var manifest = NewManifest(imports: ["rmc_default_female_soldier_2"]);

        var result = ImportedPrefabValidator.Validate(_projectDir, manifest);

        Assert.Null(result);
    }

    [Fact]
    public void ImportedNotDeclared_ButUnreferenced_ReturnsNull()
    {
        var guid = NewGuid();
        SeedImported("rmc_default_female_soldier_2", guid);

        var manifest = NewManifest(imports: null);

        var result = ImportedPrefabValidator.Validate(_projectDir, manifest);

        Assert.Null(result);
    }

    [Fact]
    public void ImportedNotDeclared_AndReferenced_ReturnsError()
    {
        var guid = NewGuid();
        SeedImported("rmc_default_female_soldier_2", guid);
        WriteFile(Path.Combine(_assetsDir, "Prefabs", "my_char.prefab"),
            $"m_Script: {{fileID: 1, guid: {guid}, type: 3}}");

        var manifest = NewManifest(imports: null);

        var result = ImportedPrefabValidator.Validate(_projectDir, manifest);

        Assert.NotNull(result);
        Assert.Contains("'imports'", result);
        Assert.Contains("rmc_default_female_soldier_2", result);
        Assert.Contains(Path.Combine("unity", "Assets", "Prefabs", "my_char.prefab"), result);
        Assert.Contains("or remove", result);
    }

    [Fact]
    public void MultipleViolations_GroupedByFile()
    {
        var soldierGuid = NewGuid();
        var rifleGuid = NewGuid();
        SeedImported("soldier", soldierGuid);
        SeedImported("rifle", rifleGuid);

        // Same file references both.
        WriteFile(Path.Combine(_assetsDir, "Prefabs", "loadout.prefab"),
            $"a: {{guid: {soldierGuid}, type: 3}}\nb: {{guid: {rifleGuid}, type: 3}}");

        var manifest = NewManifest(imports: null);

        var result = ImportedPrefabValidator.Validate(_projectDir, manifest);

        Assert.NotNull(result);
        Assert.Contains("- rifle", result);
        Assert.Contains("- soldier", result);
        Assert.Contains("Imported/rifle", result);
        Assert.Contains("Imported/soldier", result);
    }

    [Fact]
    public void ReferenceInsideImported_DoesNotTriggerSelfViolation()
    {
        var guid = NewGuid();
        SeedImported("soldier", guid);
        // A .prefab inside Imported/soldier/ that references the same guid
        // (typical for AssetRipper cross-references within a rip). The scan
        // should ignore the Imported/ tree entirely.
        WriteFile(Path.Combine(_importedDir, "soldier", "GameObject", "inner.prefab"),
            $"a: {{guid: {guid}, type: 3}}");

        var manifest = NewManifest(imports: null);

        var result = ImportedPrefabValidator.Validate(_projectDir, manifest);

        Assert.Null(result);
    }

    [Fact]
    public void DeclaredCaseInsensitive_AcceptsMixedCaseManifestEntry()
    {
        var guid = NewGuid();
        SeedImported("soldier", guid);
        WriteFile(Path.Combine(_assetsDir, "Prefabs", "x.prefab"),
            $"a: {{guid: {guid}, type: 3}}");

        var manifest = NewManifest(imports: ["SOLDIER"]);

        var result = ImportedPrefabValidator.Validate(_projectDir, manifest);

        Assert.Null(result);
    }

    [Fact]
    public void NonTextSerialisedExtensions_AreSkipped()
    {
        var guid = NewGuid();
        SeedImported("soldier", guid);
        // .cs and .unity are not in the scan extension set. Even though the
        // file contains the guid text, the validator must not flag it.
        WriteFile(Path.Combine(_assetsDir, "Scripts", "MyScript.cs"),
            $"// guid: {guid} mentioned in a comment");

        var manifest = NewManifest(imports: null);

        var result = ImportedPrefabValidator.Validate(_projectDir, manifest);

        Assert.Null(result);
    }

    private void SeedImported(string subdir, string guid)
    {
        var dir = Path.Combine(_importedDir, subdir, "GameObject");
        Directory.CreateDirectory(dir);
        // Mimic AssetRipper output: an asset file plus its sibling .meta. Only
        // the .meta is consulted for the guid → subdir map.
        File.WriteAllText(Path.Combine(dir, $"{subdir}.prefab"), "stub");
        File.WriteAllText(
            Path.Combine(dir, $"{subdir}.prefab.meta"),
            $"fileFormatVersion: 2\nguid: {guid}\n");
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static ModManifest NewManifest(List<string>? imports) => new()
    {
        Name = "test-mod",
        Imports = imports,
    };

    private static string NewGuid() => Guid.NewGuid().ToString("N");
}
