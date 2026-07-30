using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Code;

namespace Jiangyu.Core.Tests.Code;

/// <summary>
/// The code/ build is skipped when its recorded input key matches. The key must move
/// on every input the build actually reads, or the skip ships a stale DLL silently.
/// </summary>
public sealed class CodeBuildSkipTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectDir;
    private readonly string _codeDir;
    private readonly string _sdkDir;
    private readonly string _gameDir;

    public CodeBuildSkipTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"jiangyu-code-skip-{Guid.NewGuid():N}");
        _projectDir = Path.Combine(_root, "mod");
        _codeDir = Path.Combine(_projectDir, "code");
        _sdkDir = Path.Combine(_root, "sdk");
        _gameDir = Path.Combine(_root, "game");
        Directory.CreateDirectory(_codeDir);
        Directory.CreateDirectory(_sdkDir);
        Directory.CreateDirectory(_gameDir);
        File.WriteAllText(Path.Combine(_codeDir, "Mod.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_codeDir, "Mod.cs"), "class Mod { }");
        File.WriteAllText(Path.Combine(_sdkDir, "Jiangyu.Sdk.dll"), "sdk bytes");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Key(string? codeDir = null, string? gameDir = null, string? sdkDir = null, bool devSources = true, string? context = "game-1.0")
        => CodeBuildService.ComputeBuildKey(codeDir ?? _codeDir, gameDir ?? _gameDir, sdkDir ?? _sdkDir, devSources, context);

    [Fact]
    public void KeyIsStableForUnchangedInputs()
    {
        Assert.Equal(Key(), Key());
    }

    [Fact]
    public void KeyMovesOnEveryBuildInput()
    {
        var baseline = Key();

        File.WriteAllText(Path.Combine(_codeDir, "Mod.cs"), "class Mod { int x; }");
        var sourceEdit = Key();
        Assert.NotEqual(baseline, sourceEdit);

        File.WriteAllText(Path.Combine(_sdkDir, "Jiangyu.Sdk.dll"), "new sdk bytes");
        var sdkChange = Key();
        Assert.NotEqual(sourceEdit, sdkChange);

        Assert.NotEqual(sdkChange, Key(devSources: false));
        Assert.NotEqual(sdkChange, Key(context: "game-2.0"));
        Assert.NotEqual(sdkChange, Key(gameDir: Path.Combine(_root, "other-game")));
    }

    [Fact]
    public void KeyIgnoresBuildOutputDirectories()
    {
        var baseline = Key();

        var objDir = Path.Combine(_codeDir, "obj");
        var binDir = Path.Combine(_codeDir, "bin", "Release");
        Directory.CreateDirectory(objDir);
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(objDir, "generated.cs"), "// per-build");
        File.WriteAllText(Path.Combine(binDir, "Mod.dll"), "output");

        Assert.Equal(baseline, Key());
    }

    [Fact]
    public async Task MatchingKeyAndPresentDll_SkipsTheBuild()
    {
        // The csproj here is not buildable, so a Success result is proof the dotnet
        // build never ran and the recorded outputs were reused.
        var binDir = Path.Combine(_codeDir, "bin", "Release");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "Mod.dll"), "not a real assembly");
        var stateDir = Path.Combine(_projectDir, ".jiangyu");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(stateDir, "code_build_state"), Key());

        var result = await new CodeBuildService(NullLogSink.Instance)
            .BuildAsync(_projectDir, _gameDir, _sdkDir, devSources: true, cacheContext: "game-1.0");

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal([Path.Combine(binDir, "Mod.dll")], result.DllPaths);
    }

    [Fact]
    public async Task StaleKey_DoesNotSkip()
    {
        var binDir = Path.Combine(_codeDir, "bin", "Release");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "Mod.dll"), "not a real assembly");
        var stateDir = Path.Combine(_projectDir, ".jiangyu");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(stateDir, "code_build_state"), Key());

        // The source moved after the key was recorded, so the skip must not fire. The
        // unbuildable csproj then fails the real build, which is the proof it ran.
        File.WriteAllText(Path.Combine(_codeDir, "Mod.cs"), "class Mod { int changed; }");
        var result = await new CodeBuildService(NullLogSink.Instance)
            .BuildAsync(_projectDir, _gameDir, _sdkDir, devSources: true, cacheContext: "game-1.0");

        Assert.NotNull(result);
        Assert.False(result!.Success);
    }
}
