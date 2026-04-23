using Jiangyu.Core.Config;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Tests.Config;

public sealed class ProjectScaffoldTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectScaffoldTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"jiangyu-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task InitAsync_CreatesManifestAndGitignore()
    {
        var projectDir = Path.Combine(_tempDir, "TestMod");
        Directory.CreateDirectory(projectDir);

        var result = await ProjectScaffold.InitAsync(projectDir);

        Assert.Equal("TestMod", result);
        Assert.True(File.Exists(Path.Combine(projectDir, ModManifest.FileName)));
        Assert.True(File.Exists(Path.Combine(projectDir, ".gitignore")));
    }

    [Fact]
    public async Task InitAsync_ManifestContainsProjectName()
    {
        var projectDir = Path.Combine(_tempDir, "MyGreatMod");
        Directory.CreateDirectory(projectDir);

        await ProjectScaffold.InitAsync(projectDir);

        var json = await File.ReadAllTextAsync(Path.Combine(projectDir, ModManifest.FileName));
        var manifest = ModManifest.FromJson(json);
        Assert.Equal("MyGreatMod", manifest.Name);
    }

    [Fact]
    public async Task InitAsync_GitignoreContainsExpectedEntries()
    {
        var projectDir = Path.Combine(_tempDir, "Mod");
        Directory.CreateDirectory(projectDir);

        await ProjectScaffold.InitAsync(projectDir);

        var content = await File.ReadAllTextAsync(Path.Combine(projectDir, ".gitignore"));
        Assert.Contains(".jiangyu/", content);
        Assert.Contains("compiled/", content);
    }

    [Fact]
    public async Task InitAsync_DoesNotOverwriteExistingGitignore()
    {
        var projectDir = Path.Combine(_tempDir, "Mod2");
        Directory.CreateDirectory(projectDir);
        var gitignorePath = Path.Combine(projectDir, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, "custom\n");

        await ProjectScaffold.InitAsync(projectDir);

        var content = await File.ReadAllTextAsync(gitignorePath);
        Assert.Equal("custom\n", content);
    }

    [Fact]
    public async Task InitAsync_ThrowsWhenManifestAlreadyExists()
    {
        var projectDir = Path.Combine(_tempDir, "Existing");
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(Path.Combine(projectDir, ModManifest.FileName), "{}");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProjectScaffold.InitAsync(projectDir));
    }
}
