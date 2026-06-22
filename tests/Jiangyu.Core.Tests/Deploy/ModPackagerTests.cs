using System.IO.Compression;
using Jiangyu.Core.Deploy;

namespace Jiangyu.Core.Tests.Deploy;

public class ModPackagerTests : IDisposable
{
    private readonly string _projectDir;

    public ModPackagerTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), $"jiangyu-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectDir))
            Directory.Delete(_projectDir, recursive: true);
    }

    private void SeedCompiled(string name, string version)
    {
        var compiled = Path.Combine(_projectDir, "compiled");
        Directory.CreateDirectory(Path.Combine(compiled, "bundles"));
        File.WriteAllText(Path.Combine(compiled, "jiangyu.json"), $$"""{"name":"{{name}}","version":"{{version}}"}""");
        File.WriteAllText(Path.Combine(compiled, "templates.json"), "{}");
        File.WriteAllText(Path.Combine(compiled, "bundles", "thing.bundle"), "bundle-bytes");
    }

    [Fact]
    public void PackProject_ZipsCompiledUnderTopLevelModFolder()
    {
        SeedCompiled("MyMod", "1.2.0");

        var result = ModPackager.PackProject(_projectDir);

        Assert.Equal("MyMod", result.ModName);
        Assert.Equal("1.2.0", result.Version);
        Assert.Equal(Path.Combine(_projectDir, "MyMod-1.2.0.zip"), result.ArchivePath);
        Assert.True(File.Exists(result.ArchivePath));

        using var zip = ZipFile.OpenRead(result.ArchivePath);
        var entries = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            ["MyMod/bundles/thing.bundle", "MyMod/jiangyu.json", "MyMod/templates.json"],
            entries);
    }

    [Fact]
    public void PackProject_HonoursOutputDirectory()
    {
        SeedCompiled("MyMod", "0.1.0");
        var outDir = Path.Combine(_projectDir, "dist");

        var result = ModPackager.PackProject(_projectDir, outDir);

        Assert.Equal(Path.Combine(outDir, "MyMod-0.1.0.zip"), result.ArchivePath);
        Assert.True(File.Exists(result.ArchivePath));
    }

    [Fact]
    public void PackProject_ThrowsWhenNotCompiled()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ModPackager.PackProject(_projectDir));
        Assert.Contains("compile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackProject_OverwritesExistingArchive()
    {
        SeedCompiled("MyMod", "1.0.0");
        var first = ModPackager.PackProject(_projectDir);
        File.WriteAllText(first.ArchivePath, "stale");

        var second = ModPackager.PackProject(_projectDir);

        // A valid zip again, not the stale bytes.
        using var zip = ZipFile.OpenRead(second.ArchivePath);
        Assert.NotEmpty(zip.Entries);
    }
}
