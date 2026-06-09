using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Code;
using Xunit;

namespace Jiangyu.Core.Tests.Code;

public sealed class CodeBuildServiceTests : IDisposable
{
    private readonly string _projectDir =
        Path.Combine(Path.GetTempPath(), $"jiangyu-codebuild-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_projectDir))
            Directory.Delete(_projectDir, recursive: true);
    }

    [Fact]
    public async Task BuildAsync_ReturnsNull_WhenThereIsNoCodeDir()
    {
        Directory.CreateDirectory(_projectDir);

        var result = await new CodeBuildService(NullLogSink.Instance).BuildAsync(_projectDir, "/game", "/sdk");

        Assert.Null(result);
    }

    [Fact]
    public async Task BuildAsync_ReturnsNull_ForAScaffoldedCsprojWithNoCSharpSource()
    {
        var codeDir = Path.Combine(_projectDir, "code");
        Directory.CreateDirectory(codeDir);
        File.WriteAllText(Path.Combine(codeDir, "Test.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        // Generated sources the SDK leaves under obj/ must not count as real code.
        Directory.CreateDirectory(Path.Combine(codeDir, "obj"));
        File.WriteAllText(Path.Combine(codeDir, "obj", "Test.AssemblyInfo.cs"), "// generated");

        var result = await new CodeBuildService(NullLogSink.Instance).BuildAsync(_projectDir, "/game", "/sdk");

        Assert.Null(result);
    }

    [Fact]
    public async Task BuildAsync_ProceedsPastTheSourceGate_WhenAHandWrittenCSharpFileExists()
    {
        var codeDir = Path.Combine(_projectDir, "code");
        Directory.CreateDirectory(codeDir);
        File.WriteAllText(Path.Combine(codeDir, "Test.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(codeDir, "Mod.cs"), "public sealed class Mod { }");

        // An empty SDK path makes BuildAsync fail at the SDK-path check, which only runs
        // after the source gate. A non-null failure therefore proves a real .cs is treated
        // as a code project rather than skipped.
        var result = await new CodeBuildService(NullLogSink.Instance).BuildAsync(_projectDir, "/game", "");

        Assert.NotNull(result);
        Assert.False(result!.Success);
    }
}
