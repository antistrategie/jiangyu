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

    [Fact]
    public void Build_DiscoversTemplateOnlyModWithNoBundlesDir()
    {
        var modDir = Path.Combine(_modsDir, "10-patchonly");
        Directory.CreateDirectory(modDir);
        File.WriteAllText(Path.Combine(modDir, "jiangyu.json"), BuildManifestJson("PatchOnly", null));
        // No bundles/ subfolder at all — a template- or code-only mod.

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        var mod = Assert.Single(plan.LoadableMods);
        Assert.Equal("PatchOnly", mod.Name);
        Assert.Empty(mod.BundlePaths);
        Assert.Empty(plan.BlockedMods);
    }

    [Fact]
    public void Build_AcceptsSatisfiedVersionConstraint()
    {
        CreateMod("10-base", "Base", version: "1.2.0");
        CreateMod("20-addon", "Addon", depends: ["Base >= 1.0.0"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["Base", "Addon"], plan.LoadableMods.Select(mod => mod.Name).ToArray());
        Assert.Empty(plan.BlockedMods);
    }

    [Fact]
    public void Build_BlocksUnsatisfiedVersionConstraint()
    {
        CreateMod("10-base", "Base", version: "0.9.0");
        CreateMod("20-addon", "Addon", depends: ["Base >= 1.0.0"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        var loadable = Assert.Single(plan.LoadableMods);
        Assert.Equal("Base", loadable.Name);

        var blocked = Assert.Single(plan.BlockedMods);
        Assert.Equal("Addon", blocked.Name);
        Assert.Contains("Base >= 1.0.0", blocked.Reason);
        Assert.Contains("found Base 0.9.0", blocked.Reason);
    }

    [Fact]
    public void Build_BlocksInvalidVersionConstraint()
    {
        CreateMod("10-base", "Base", version: "1.0.0");
        CreateMod("20-addon", "Addon", depends: ["Base >= notaversion"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        var blocked = plan.BlockedMods.Single(mod => mod.Name == "Addon");
        Assert.Contains("invalid version", blocked.Reason);
        Assert.DoesNotContain(plan.LoadableMods, mod => mod.Name == "Addon");
    }

    [Fact]
    public void Build_EnforcesJiangyuFloorAgainstLoaderVersion()
    {
        CreateMod("10-needs-new", "NeedsNew", depends: ["Jiangyu >= 2.0.0"]);

        var blockedPlan = ModLoadPlanBuilder.Build(_modsDir, loaderVersion: "1.0.0");
        var blocked = Assert.Single(blockedPlan.BlockedMods);
        Assert.Equal("NeedsNew", blocked.Name);
        Assert.Contains("Jiangyu >= 2.0.0", blocked.Reason);

        var okPlan = ModLoadPlanBuilder.Build(_modsDir, loaderVersion: "2.1.0");
        Assert.Single(okPlan.LoadableMods);
        Assert.Empty(okPlan.BlockedMods);
    }

    [Fact]
    public void Build_JiangyuFloorIsPresenceOnlyWithoutLoaderVersion()
    {
        CreateMod("10-needs-new", "NeedsNew", depends: ["Jiangyu >= 2.0.0"]);

        // No loader version supplied (offline use): the Jiangyu floor degrades to presence.
        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Single(plan.LoadableMods);
        Assert.Empty(plan.BlockedMods);
    }

    [Fact]
    public void Build_BlocksWhenConflictingModPresent()
    {
        CreateMod("10-a", "A", conflicts: ["B"]);
        CreateMod("20-b", "B");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        var loadable = Assert.Single(plan.LoadableMods);
        Assert.Equal("B", loadable.Name);

        var blocked = Assert.Single(plan.BlockedMods);
        Assert.Equal("A", blocked.Name);
        Assert.Contains("conflicts with B", blocked.Reason);
    }

    [Fact]
    public void Build_ConflictRangeBlocksOnlyMatchingVersions()
    {
        CreateMod("10-a", "A", conflicts: ["B < 2.0.0"]);
        CreateMod("20-b", "B", version: "1.5.0");

        var blockedPlan = ModLoadPlanBuilder.Build(_modsDir);
        Assert.Contains(blockedPlan.BlockedMods, mod => mod.Name == "A");

        Directory.Delete(Path.Combine(_modsDir, "20-b"), recursive: true);
        CreateMod("20-b", "B", version: "2.0.0");

        var okPlan = ModLoadPlanBuilder.Build(_modsDir);
        Assert.Equal(["A", "B"], okPlan.LoadableMods.Select(mod => mod.Name).ToArray());
        Assert.Empty(okPlan.BlockedMods);
    }

    [Fact]
    public void Build_NoConflictWhenTargetAbsent()
    {
        CreateMod("10-a", "A", conflicts: ["Ghost"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Single(plan.LoadableMods);
        Assert.Empty(plan.BlockedMods);
    }

    public void Dispose()
    {
        if (Directory.Exists(_modsDir))
            Directory.Delete(_modsDir, recursive: true);
    }

    private void CreateMod(
        string relativeDir,
        string name,
        string[]? depends = null,
        string? version = null,
        string[]? conflicts = null)
    {
        var modDir = Path.Combine(_modsDir, relativeDir);
        Directory.CreateDirectory(modDir);
        File.WriteAllText(
            Path.Combine(modDir, "jiangyu.json"),
            BuildManifestJson(name, depends, version, conflicts));
        var bundlesDir = Path.Combine(modDir, "bundles");
        Directory.CreateDirectory(bundlesDir);
        File.WriteAllText(Path.Combine(bundlesDir, $"{name}.bundle"), "bundle");
    }

    private static string BuildManifestJson(string name, string[]? depends, string? version = null, string[]? conflicts = null)
    {
        var fields = new List<string> { $"\"name\": \"{name}\"" };
        if (version != null)
            fields.Add($"\"version\": \"{version}\"");
        if (depends is { Length: > 0 })
            fields.Add($"\"depends\": [{string.Join(", ", depends.Select(dep => $"\"{dep}\""))}]");
        if (conflicts is { Length: > 0 })
            fields.Add($"\"conflicts\": [{string.Join(", ", conflicts.Select(con => $"\"{con}\""))}]");
        return $"{{\n  {string.Join(",\n  ", fields)}\n}}";
    }
}
