using Jiangyu.Core.Compile;
using Jiangyu.Shared.Bundles;
using Xunit;

namespace Jiangyu.Core.Tests.Compile;

public sealed class CompiledOutputTests : IDisposable
{
    private readonly string _projectDir =
        Path.Combine(Path.GetTempPath(), $"jiangyu-compiled-output-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_projectDir))
            Directory.Delete(_projectDir, recursive: true);
    }

    [Fact]
    public void Reset_WipesEveryPriorArtefactAndLeavesAnEmptyBundlesDir()
    {
        var compiled = Path.Combine(_projectDir, "compiled");
        Directory.CreateDirectory(Path.Combine(compiled, CompiledLayout.BundlesDirName));
        Directory.CreateDirectory(Path.Combine(compiled, CompiledLayout.CodeDirName));
        // Everything a previous compile (or an older flat layout) might have left behind.
        File.WriteAllText(Path.Combine(compiled, "jiangyu.json"), "{}");
        File.WriteAllText(Path.Combine(compiled, "templates.json"), "{}");
        File.WriteAllText(Path.Combine(compiled, "stale-flat.bundle"), "x");
        File.WriteAllText(Path.Combine(compiled, CompiledLayout.BundlesDirName, "removed-addition.bundle"), "x");
        File.WriteAllText(Path.Combine(compiled, CompiledLayout.CodeDirName, "Old.Code.dll"), "x");

        var outputDir = CompiledOutput.Reset(_projectDir);

        Assert.Equal(compiled, outputDir);
        Assert.True(Directory.Exists(Path.Combine(compiled, CompiledLayout.BundlesDirName)));
        // Nothing from the previous compile survives the wipe.
        Assert.Empty(Directory.GetFiles(compiled, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Reset_CreatesTheTreeWhenNoneExists()
    {
        var outputDir = CompiledOutput.Reset(_projectDir);

        Assert.True(Directory.Exists(outputDir));
        Assert.True(Directory.Exists(Path.Combine(outputDir, CompiledLayout.BundlesDirName)));
    }
}
