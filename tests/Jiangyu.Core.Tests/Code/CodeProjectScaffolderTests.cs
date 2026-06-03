using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Code;
using Xunit;

namespace Jiangyu.Core.Tests.Code;

public sealed class CodeProjectScaffolderTests : IDisposable
{
    private const string GameDir = "/games/Menace";
    private const string SdkDir = "/sdk/net6.0";

    private readonly string _tempRoot;

    public CodeProjectScaffolderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"jiangyu-code-scaffolder-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private string CodeDir => Path.Combine(_tempRoot, "code");

    [Fact]
    public void FreshInit_CreatesManagedBuildFilesAndSeedsProject()
    {
        new CodeProjectScaffolder(new NullLog()).Init(_tempRoot, GameDir, SdkDir);

        Assert.True(Directory.Exists(CodeDir));
        Assert.True(File.Exists(Path.Combine(CodeDir, "Directory.Build.props")));
        Assert.True(File.Exists(Path.Combine(CodeDir, "Directory.Build.targets")));
        Assert.True(File.Exists(Path.Combine(CodeDir, ".gitignore")));
        Assert.True(File.Exists(Path.Combine(CodeDir, "README.md")));
        // Exactly one seeded project file, and no example source stub.
        Assert.Single(Directory.GetFiles(CodeDir, "*.csproj"));
        Assert.Empty(Directory.GetFiles(CodeDir, "*.cs"));
        // A solution at the mod root, so an IDE opened there loads the project.
        Assert.Single(Directory.GetFiles(_tempRoot, "*.slnx"));
    }

    [Fact]
    public void FreshInit_WritesRootSolutionReferencingCodeProject()
    {
        new CodeProjectScaffolder(new NullLog()).Init(_tempRoot, GameDir, SdkDir);

        var sln = File.ReadAllText(Directory.GetFiles(_tempRoot, "*.slnx").Single());
        var csprojName = Path.GetFileName(Directory.GetFiles(CodeDir, "*.csproj").Single());
        Assert.Contains("<Solution>", sln);
        Assert.Contains($"code/{csprojName}", sln);
    }

    [Fact]
    public void Init_PreservesExistingRootSolution()
    {
        var existingSln = Path.Combine(_tempRoot, "Custom.sln");
        File.WriteAllText(existingSln, "// my solution");

        new CodeProjectScaffolder(new NullLog()).Init(_tempRoot, GameDir, SdkDir);

        Assert.Equal("// my solution", File.ReadAllText(existingSln));
        Assert.Single(Directory.GetFiles(_tempRoot, "*.sln"));
    }

    [Fact]
    public void SeededProject_IsMinimal_RefsComeFromManagedTargets()
    {
        new CodeProjectScaffolder(new NullLog()).Init(_tempRoot, GameDir, SdkDir);

        var csproj = File.ReadAllText(Directory.GetFiles(CodeDir, "*.csproj").Single());
        // The seeded project carries no hardcoded references; they are injected
        // by the managed Directory.Build.targets.
        Assert.DoesNotContain("<Reference", csproj);
        Assert.DoesNotContain("HintPath", csproj);

        var targets = File.ReadAllText(Path.Combine(CodeDir, "Directory.Build.targets"));
        Assert.Contains("Jiangyu.Sdk", targets);
        Assert.Contains("$(JiangyuSdkDir)", targets);
        Assert.Contains("$(GameAssembliesDir)", targets);
        Assert.Contains("Assembly-CSharp", targets);
    }

    [Fact]
    public void LocalProps_CarriesResolvedPaths()
    {
        new CodeProjectScaffolder(new NullLog()).Init(_tempRoot, GameDir, SdkDir);

        var localProps = File.ReadAllText(Path.Combine(CodeDir, "local.props"));
        Assert.Contains($"<GameDir>{GameDir}</GameDir>", localProps);
        Assert.Contains($"<JiangyuSdkDir>{SdkDir}</JiangyuSdkDir>", localProps);
    }

    [Fact]
    public void LocalProps_OmittedWhenPathsUnresolved()
    {
        new CodeProjectScaffolder(new NullLog()).Init(_tempRoot, gameDir: null, sdkDir: null);

        Assert.False(File.Exists(Path.Combine(CodeDir, "local.props")));
    }

    [Fact]
    public void Init_PreservesExistingProjectAndSource()
    {
        Directory.CreateDirectory(CodeDir);
        var existingCsproj = Path.Combine(CodeDir, "Existing.csproj");
        var existingSource = Path.Combine(CodeDir, "MyMod.cs");
        File.WriteAllText(existingCsproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(existingSource, "// my mod");

        new CodeProjectScaffolder(new NullLog()).Init(_tempRoot, GameDir, SdkDir);

        // Existing project and source untouched; no Mod.cs seeded over them.
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\" />", File.ReadAllText(existingCsproj));
        Assert.Equal("// my mod", File.ReadAllText(existingSource));
        Assert.False(File.Exists(Path.Combine(CodeDir, "Mod.cs")));
        // Managed build files are still written.
        Assert.True(File.Exists(Path.Combine(CodeDir, "Directory.Build.props")));
    }

    [Fact]
    public void Rerun_OnUntouchedTree_ReportsNoChanges()
    {
        var scaffolder = new CodeProjectScaffolder(new NullLog());
        scaffolder.Init(_tempRoot, GameDir, SdkDir);

        var result = scaffolder.Init(_tempRoot, GameDir, SdkDir);

        Assert.Empty(result.CreatedFiles);
        Assert.Empty(result.OverwrittenFiles);
    }

    [Fact]
    public void Rerun_OverwritesDriftedManagedFile()
    {
        var scaffolder = new CodeProjectScaffolder(new NullLog());
        scaffolder.Init(_tempRoot, GameDir, SdkDir);

        var targetsPath = Path.Combine(CodeDir, "Directory.Build.targets");
        File.WriteAllText(targetsPath, "<Project />");

        var result = scaffolder.Init(_tempRoot, GameDir, SdkDir);

        Assert.Contains("Jiangyu.Sdk", File.ReadAllText(targetsPath));
        Assert.Contains(targetsPath, result.OverwrittenFiles);
    }

    [Fact]
    public void Gitignore_CoversBuildOutputsAndLocalProps()
    {
        new CodeProjectScaffolder(new NullLog()).Init(_tempRoot, GameDir, SdkDir);

        var gitignore = File.ReadAllText(Path.Combine(CodeDir, ".gitignore"));
        Assert.Contains("bin/", gitignore);
        Assert.Contains("obj/", gitignore);
        Assert.Contains("local.props", gitignore);
    }

    private sealed class NullLog : ILogSink
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
    }
}
