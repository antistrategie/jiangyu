using Jiangyu.Shared.Bundles;
using Xunit;

namespace Jiangyu.Loader.Tests;

public sealed class ModLoadPlanBuilderTests : IDisposable
{
    private readonly string _modsDir;

    public ModLoadPlanBuilderTests()
    {
        _modsDir = Path.Combine(Path.GetTempPath(), $"jiangyu-loader-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_modsDir);
    }

    [Fact]
    public void Build_SortsLoadableModsByLexicalFolderPath()
    {
        CreateMod("20-addon", "Addon", depends: ["Base"]);
        CreateMod("10-base", "Base");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["Base", "Addon"], plan.LoadableMods.Select(mod => mod.Name).ToArray());
        Assert.Empty(plan.BlockedMods);
    }

    [Fact]
    public void Build_TreatsJiangyuDependencyAsPresent()
    {
        CreateMod("10-example", "Example", depends: ["Jiangyu >= 1.0.0"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        var mod = Assert.Single(plan.LoadableMods);
        Assert.Equal("Example", mod.Name);
        var dependency = Assert.Single(mod.Dependencies);
        Assert.Equal("Jiangyu", dependency.Name);
        Assert.Equal("1.0.0", dependency.Constraint);
        Assert.Empty(plan.BlockedMods);
    }

    [Fact]
    public void Build_BlocksModsWithMissingRequiredDependenciesBeforeLoad()
    {
        CreateMod("20-addon", "Addon", depends: ["Base", "MissingMod >= 2.0"]);
        CreateMod("10-base", "Base");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        var loadable = Assert.Single(plan.LoadableMods);
        Assert.Equal("Base", loadable.Name);

        var blocked = Assert.Single(plan.BlockedMods);
        Assert.Equal("Addon", blocked.Name);
        Assert.Contains("MissingMod >= 2.0", blocked.Reason);
    }

    [Fact]
    public void Build_BlocksDuplicateManifestNames()
    {
        CreateMod("10-first", "SharedName");
        CreateMod("20-second", "SharedName");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Empty(plan.LoadableMods);
        Assert.Equal(2, plan.BlockedMods.Count);
        Assert.All(plan.BlockedMods, blocked => Assert.Contains("Duplicate manifest name", blocked.Reason));
    }

    public void Dispose()
    {
        if (Directory.Exists(_modsDir))
            Directory.Delete(_modsDir, recursive: true);
    }

    private void CreateMod(string relativeDir, string name, string[]? depends = null)
    {
        var modDir = Path.Combine(_modsDir, relativeDir);
        Directory.CreateDirectory(modDir);
        File.WriteAllText(
            Path.Combine(modDir, "jiangyu.json"),
            BuildManifestJson(name, depends));
        File.WriteAllText(Path.Combine(modDir, $"{name}.bundle"), "bundle");
    }

    private static string BuildManifestJson(string name, string[]? depends)
    {
        if (depends == null || depends.Length == 0)
            return $$"""
            {
              "name": "{{name}}"
            }
            """;

        var dependsJson = string.Join(", ", depends.Select(dep => $"\"{dep}\""));
        return $$"""
        {
          "name": "{{name}}",
          "depends": [{{dependsJson}}]
        }
        """;
    }
}
