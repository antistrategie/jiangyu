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

    [Fact]
    public void Build_LoadsRequiredDependencyBeforeDependentWhateverTheFolderOrder()
    {
        CreateMod("10-addon", "Addon", depends: ["Base"]);
        CreateMod("20-base", "Base");
        CreateMod("30-other", "Other");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["Base", "Addon", "Other"], Names(plan));
        Assert.Empty(plan.BlockedMods);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Build_OrdersAcrossADependencyChain()
    {
        CreateMod("10-top", "Top", depends: ["Middle"]);
        CreateMod("20-middle", "Middle", depends: ["Bottom"]);
        CreateMod("30-bottom", "Bottom");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["Bottom", "Middle", "Top"], Names(plan));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Build_EmitsASharedDependencyOnce()
    {
        CreateMod("10-a", "A", depends: ["B", "C"]);
        CreateMod("20-b", "B", depends: ["D"]);
        CreateMod("30-c", "C", depends: ["D"]);
        CreateMod("40-d", "D");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["D", "B", "C", "A"], Names(plan));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Build_APulledUpDependencyPassesTheUnrelatedModsBetween()
    {
        CreateMod("10-patch", "Patch", depends: ["Core"]);
        CreateMod("20-skins", "Skins");
        CreateMod("30-core", "Core");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        // Core moves up to sit before Patch, so it now loads before Skins as well.
        Assert.Equal(["Core", "Patch", "Skins"], Names(plan));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Build_KeepsRelativeOrderAmongModsNotPulledAhead()
    {
        CreateMod("10-a", "A");
        CreateMod("20-b", "B", depends: ["D"]);
        CreateMod("30-c", "C");
        CreateMod("40-d", "D");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        // D moves up to sit before B. A, B and C keep their order relative to each other.
        Assert.Equal(["A", "D", "B", "C"], Names(plan));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Build_LoadsAfterAnInstalledOptionalDependency()
    {
        CreateMod("10-addon", "Addon", optionalDepends: ["Base >= 1.0.0"]);
        CreateMod("20-base", "Base", version: "1.2.0");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["Base", "Addon"], Names(plan));
        Assert.Empty(plan.BlockedMods);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Build_LoadsWithoutAnAbsentOptionalDependency()
    {
        CreateMod("10-addon", "Addon", optionalDepends: ["Ghost"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        var mod = Assert.Single(plan.LoadableMods);
        Assert.Equal("Addon", mod.Name);
        var optional = Assert.Single(mod.OptionalDependencies);
        Assert.Equal("Ghost", optional.Name);
        Assert.Empty(plan.BlockedMods);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Build_OptionalDependencyOutsideItsConstraintNeitherBlocksNorOrders()
    {
        CreateMod("10-addon", "Addon", optionalDepends: ["Base >= 2.0.0"]);
        CreateMod("20-base", "Base", version: "1.0.0");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["Addon", "Base"], Names(plan));
        Assert.Empty(plan.BlockedMods);
        Assert.Equal(
            ["'Addon' [10-addon]: optional dependency 'Base >= 2.0.0' is not met (found Base 1.0.0), the entry is ignored."],
            plan.Warnings);
    }

    [Fact]
    public void Build_OptionalDependencyThatIsBlockedNeitherBlocksNorOrders()
    {
        CreateMod("10-addon", "Addon", optionalDepends: ["Base"]);
        CreateMod("20-base", "Base", depends: ["Ghost"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["Addon"], Names(plan));
        var blocked = Assert.Single(plan.BlockedMods);
        Assert.Equal("Base", blocked.Name);
        Assert.Equal(
            ["'Addon' [10-addon]: optional dependency 'Base' is installed but blocked, the entry is ignored."],
            plan.Warnings);
    }

    [Fact]
    public void Build_OptionalDependencyWithAMalformedManifestIsReportedAsBlocked()
    {
        CreateMod("10-addon", "Addon", optionalDepends: ["Base"]);
        CreateMod("20-base", "Base", conflicts: [""]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["Addon"], Names(plan));
        var blocked = Assert.Single(plan.BlockedMods);
        Assert.Equal("Base", blocked.Name);
        Assert.Equal(
            ["'Addon' [10-addon]: optional dependency 'Base' is installed but blocked, the entry is ignored."],
            plan.Warnings);
    }

    [Fact]
    public void Build_OptionalDependencyBlockedByACycleIsReported()
    {
        CreateMod("10-a", "A", depends: ["B"]);
        CreateMod("20-b", "B", depends: ["A"]);
        CreateMod("30-c", "C", optionalDepends: ["A"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["C"], Names(plan));
        Assert.Equal(["A", "B"], plan.BlockedMods.Select(mod => mod.Name).ToArray());
        Assert.Equal(
            ["'C' [30-c]: optional dependency 'A' is installed but blocked, the entry is ignored."],
            plan.Warnings);
    }

    [Fact]
    public void Build_LeavesNoWarningForAModBlockedAfterItsOptionalEntriesWereJudged()
    {
        CreateMod("10-a", "A", depends: ["B"], optionalDepends: ["C >= 2.0.0"]);
        CreateMod("20-b", "B", depends: ["A"]);
        CreateMod("30-c", "C", version: "1.0.0");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["C"], Names(plan));
        Assert.Equal(["A", "B"], plan.BlockedMods.Select(mod => mod.Name).ToArray());
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Build_BlocksInvalidOptionalDependencyEntry()
    {
        CreateMod("10-addon", "Addon", optionalDepends: ["Base >= notaversion"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Empty(plan.LoadableMods);
        var blocked = Assert.Single(plan.BlockedMods);
        Assert.Equal("Optional dependency entry 'Base >= notaversion' has an invalid version 'notaversion'.", blocked.Reason);
    }

    [Fact]
    public void Build_RequiredDependencyWithAMalformedManifestIsReportedAsBlocked()
    {
        CreateMod("10-addon", "Addon", depends: ["Base"]);
        CreateMod("20-base", "Base", conflicts: [""]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Empty(plan.LoadableMods);
        var addon = plan.BlockedMods.Single(mod => mod.Name == "Addon");
        Assert.Equal("Cannot load 'Addon': requires Base, which is blocked.", addon.Reason);
    }

    [Fact]
    public void Build_BlocksDependentsOfABlockedMod()
    {
        CreateMod("10-base", "Base", depends: ["Ghost"]);
        CreateMod("20-addon", "Addon", depends: ["Base"]);
        CreateMod("30-extra", "Extra", depends: ["Addon"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Empty(plan.LoadableMods);
        Assert.Equal(["Base", "Addon", "Extra"], plan.BlockedMods.Select(mod => mod.Name).ToArray());
        Assert.Equal("Cannot load 'Base': requires Ghost.", plan.BlockedMods[0].Reason);
        Assert.Equal("Cannot load 'Addon': requires Base, which is blocked.", plan.BlockedMods[1].Reason);
        Assert.Equal("Cannot load 'Extra': requires Addon, which is blocked.", plan.BlockedMods[2].Reason);
    }

    [Fact]
    public void Build_BlocksDependentsOfABlockedModAgainstFolderOrder()
    {
        CreateMod("10-extra", "Extra", depends: ["Addon"]);
        CreateMod("20-addon", "Addon", depends: ["Base"]);
        CreateMod("30-base", "Base", depends: ["Ghost"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Empty(plan.LoadableMods);
        Assert.Equal(["Extra", "Addon", "Base"], plan.BlockedMods.Select(mod => mod.Name).ToArray());
        Assert.Equal("Cannot load 'Extra': requires Addon, which is blocked.", plan.BlockedMods[0].Reason);
        Assert.Equal("Cannot load 'Addon': requires Base, which is blocked.", plan.BlockedMods[1].Reason);
    }

    [Fact]
    public void Build_BlocksARequiredDependencyCycleAndWhatNeedsIt()
    {
        CreateMod("10-a", "A", depends: ["B"]);
        CreateMod("20-b", "B", depends: ["A"]);
        CreateMod("30-c", "C", depends: ["A"]);
        CreateMod("40-d", "D");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["D"], Names(plan));
        Assert.Equal(["A", "B", "C"], plan.BlockedMods.Select(mod => mod.Name).ToArray());
        Assert.Equal("Cannot load 'A': dependency cycle with 'B'.", plan.BlockedMods[0].Reason);
        Assert.Equal("Cannot load 'B': dependency cycle with 'A'.", plan.BlockedMods[1].Reason);
        Assert.Equal("Cannot load 'C': requires A, which is blocked.", plan.BlockedMods[2].Reason);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Build_ReportsEachRequiredCycleApart()
    {
        CreateMod("10-a", "A", depends: ["B"]);
        CreateMod("20-b", "B", depends: ["A"]);
        CreateMod("30-c", "C", depends: ["D"]);
        CreateMod("40-d", "D", depends: ["C"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Empty(plan.LoadableMods);
        Assert.Equal("Cannot load 'A': dependency cycle with 'B'.", plan.BlockedMods.Single(mod => mod.Name == "A").Reason);
        Assert.Equal("Cannot load 'C': dependency cycle with 'D'.", plan.BlockedMods.Single(mod => mod.Name == "C").Reason);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Build_BreaksACycleAtItsOptionalEdge()
    {
        CreateMod("10-a", "A", optionalDepends: ["B"]);
        CreateMod("20-b", "B", depends: ["A"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["A", "B"], Names(plan));
        Assert.Empty(plan.BlockedMods);
        Assert.Equal(
            ["'A' [10-a]: optional dependency 'B' is ignored for ordering, honouring it would form a cycle."],
            plan.Warnings);
    }

    [Fact]
    public void Build_DropsOnlyTheOptionalEdgeThatClosesTheCycle()
    {
        CreateMod("10-a", "A", optionalDepends: ["D"]);
        CreateMod("20-b", "B", optionalDepends: ["D"]);
        CreateMod("30-c", "C");
        CreateMod("40-d", "D", depends: ["A", "C"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        // A's edge closes the cycle A, D, A and is dropped. B's edge into D stands, so B
        // loads after D.
        Assert.Equal(["A", "C", "D", "B"], Names(plan));
        Assert.Equal(
            ["'A' [10-a]: optional dependency 'D' is ignored for ordering, honouring it would form a cycle."],
            plan.Warnings);
    }

    [Fact]
    public void Build_KeepsAnInnocentOptionalEdgeIntoAMixedCycle()
    {
        CreateMod("10-c", "C", optionalDepends: ["B"]);
        CreateMod("20-a", "A", depends: ["B"]);
        CreateMod("30-b", "B", optionalDepends: ["A"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["B", "C", "A"], Names(plan));
        Assert.Equal(
            ["'B' [30-b]: optional dependency 'A' is ignored for ordering, honouring it would form a cycle."],
            plan.Warnings);
    }

    [Fact]
    public void Build_BreaksAnOptionalRingAtOneEdgePreferringFolderOrder()
    {
        CreateMod("10-a", "A", optionalDepends: ["B"]);
        CreateMod("20-b", "B", optionalDepends: ["C"]);
        CreateMod("30-c", "C", optionalDepends: ["A"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        // The edge from A to B runs against folder order and is the one dropped. The
        // other two are honoured: C after A, B after C.
        Assert.Equal(["A", "C", "B"], Names(plan));
        Assert.Equal(
            ["'A' [10-a]: optional dependency 'B' is ignored for ordering, honouring it would form a cycle."],
            plan.Warnings);
    }

    [Fact]
    public void Build_MutualOptionalDependenciesKeepFolderOrderWithOneWarning()
    {
        CreateMod("10-a", "A", optionalDepends: ["B"]);
        CreateMod("20-b", "B", optionalDepends: ["A"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["A", "B"], Names(plan));
        Assert.Equal(
            ["'A' [10-a]: optional dependency 'B' is ignored for ordering, honouring it would form a cycle."],
            plan.Warnings);
    }

    [Fact]
    public void Build_IgnoresASelfDependencyWithOneWarning()
    {
        CreateMod("10-a", "A", depends: ["A"]);
        // A cycle elsewhere makes the ordering run more than one pass.
        CreateMod("20-b", "B", optionalDepends: ["C"]);
        CreateMod("30-c", "C", depends: ["B"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["A", "B", "C"], Names(plan));
        Assert.Equal(
            [
                "'A' [10-a]: dependency 'A' names the mod itself, the entry is ignored.",
                "'B' [20-b]: optional dependency 'C' is ignored for ordering, honouring it would form a cycle.",
            ],
            plan.Warnings);
    }

    [Fact]
    public void Build_IgnoresASelfConflictWithAWarning()
    {
        CreateMod("10-a", "A", conflicts: ["A"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["A"], Names(plan));
        Assert.Equal(["'A' [10-a]: conflict 'A' names the mod itself, the entry is ignored."], plan.Warnings);
    }

    [Fact]
    public void Build_RestoresADroppedOptionalEdgeTheFinalOrderHonours()
    {
        CreateMod("10-a", "A", version: "3.0.0", depends: ["C >= 1.0.0", "B"], optionalDepends: ["D"]);
        CreateMod("20-d", "D", version: "3.0.0", depends: ["A"], optionalDepends: ["A < 3.0.0", "C"]);
        CreateMod("30-c", "C", version: "2.0.0", optionalDepends: ["A >= 2.0.0", "D"]);
        CreateMod("40-b", "B", version: "2.0.0");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        // D's optional entry for C is dropped while the cycles are being broken, but the
        // order that settles has C before D, so that entry is honoured and not reported.
        Assert.Equal(["C", "B", "A", "D"], Names(plan));
        Assert.Equal(
            [
                "'D' [20-d]: optional dependency 'A < 3.0.0' is not met (found A 3.0.0), the entry is ignored.",
                "'A' [10-a]: optional dependency 'D' is ignored for ordering, honouring it would form a cycle.",
                "'C' [30-c]: optional dependency 'A >= 2.0.0' is ignored for ordering, honouring it would form a cycle.",
                "'C' [30-c]: optional dependency 'D' is ignored for ordering, honouring it would form a cycle.",
            ],
            plan.Warnings);
    }

    [Fact]
    public void Build_IgnoresAnOptionalSelfDependencyWithAWarning()
    {
        CreateMod("10-a", "A", optionalDepends: ["A"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["A"], Names(plan));
        Assert.Equal(
            ["'A' [10-a]: optional dependency 'A' names the mod itself, the entry is ignored."],
            plan.Warnings);
    }

    [Theory]
    [InlineData("Base", "Base >= 2.0.0")]
    [InlineData("Base >= 2.0.0", "Base")]
    public void Build_JudgesEveryOptionalEntryWhateverTheirOrder(string first, string second)
    {
        CreateMod("10-addon", "Addon", optionalDepends: [first, second]);
        CreateMod("20-base", "Base", version: "1.0.0");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["Base", "Addon"], Names(plan));
        Assert.Equal(
            ["'Addon' [10-addon]: optional dependency 'Base >= 2.0.0' is not met (found Base 1.0.0), the entry is ignored."],
            plan.Warnings);
    }

    [Fact]
    public void Build_AModInBothListsIsOrderedByTheRequiredEntry()
    {
        CreateMod("10-addon", "Addon", depends: ["Base"], optionalDepends: ["Base >= 2.0.0"]);
        CreateMod("20-base", "Base", version: "1.0.0");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["Base", "Addon"], Names(plan));
        Assert.Equal(
            ["'Addon' [10-addon]: optional dependency 'Base >= 2.0.0' is not met (found Base 1.0.0), the entry is ignored."],
            plan.Warnings);
    }

    [Fact]
    public void Build_OptionalLoaderConstraintWarnsWithoutBlocking()
    {
        CreateMod("10-addon", "Addon", optionalDepends: ["Jiangyu >= 99.0.0"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir, loaderVersion: "1.4.6");

        Assert.Equal(["Addon"], Names(plan));
        Assert.Equal(
            ["'Addon' [10-addon]: optional dependency 'Jiangyu >= 99.0.0' is not met (found Jiangyu 1.4.6), the entry is ignored."],
            plan.Warnings);
    }

    [Fact]
    public void Build_BlocksAModNamedAfterTheLoaderWithoutTouchingLoaderDependencies()
    {
        CreateMod("10-addon", "Addon", depends: ["Jiangyu >= 1.0.0"]);
        CreateMod("30-impostor", "Jiangyu");

        var plan = ModLoadPlanBuilder.Build(_modsDir, loaderVersion: "1.4.6");

        Assert.Equal(["Addon"], Names(plan));
        var blocked = Assert.Single(plan.BlockedMods);
        Assert.Equal("30-impostor", blocked.RelativeDirectoryPath);
        Assert.Equal("Manifest name 'Jiangyu' is reserved for the loader.", blocked.Reason);
    }

    [Theory]
    [InlineData("10-a", "20-b")]
    [InlineData("20-a", "10-b")]
    public void Build_ConflictTriggersOnAnInstalledModEvenWhenThatModIsBlocked(string conflictingDir, string blockedDir)
    {
        CreateMod(conflictingDir, "A", conflicts: ["B"]);
        CreateMod(blockedDir, "B", depends: ["Ghost"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Empty(plan.LoadableMods);
        Assert.Equal("Cannot load 'A': conflicts with B, which is installed.", plan.BlockedMods.Single(mod => mod.Name == "A").Reason);
    }

    [Fact]
    public void Build_RangedConflictTriggersOnABlockedModEarlierInFolderOrder()
    {
        CreateMod("10-b", "B", version: "1.5.0", depends: ["Ghost"]);
        CreateMod("20-a", "A", conflicts: ["B < 2.0.0"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Empty(plan.LoadableMods);
        Assert.Equal("Cannot load 'A': conflicts with B < 2.0.0 (found B 1.5.0).", plan.BlockedMods.Single(mod => mod.Name == "A").Reason);
    }

    [Fact]
    public void Build_MutualConflictsBlockBothMods()
    {
        CreateMod("10-a", "A", conflicts: ["B"]);
        CreateMod("20-b", "B", conflicts: ["A"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Empty(plan.LoadableMods);
        Assert.Equal(["A", "B"], plan.BlockedMods.Select(mod => mod.Name).ToArray());
    }

    [Theory]
    [InlineData("1.5.0", true)]
    [InlineData("2.5.0", false)]
    public void Build_RangedConflictJudgesAMalformedModByItsOwnVersion(string version, bool triggers)
    {
        CreateMod("10-a", "A", conflicts: ["B < 2.0.0"]);
        CreateMod("20-b", "B", version: version, conflicts: [""]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(triggers ? [] : ["A"], Names(plan));
        if (triggers)
            Assert.Equal($"Cannot load 'A': conflicts with B < 2.0.0 (found B {version}).", plan.BlockedMods.Single(mod => mod.Name == "A").Reason);
    }

    [Fact]
    public void Build_RangedConflictTriggersWhenAnyDuplicateCopyIsInRange()
    {
        CreateMod("10-a", "A", conflicts: ["B < 2.0.0"]);
        CreateMod("20-b", "B", version: "3.0.0");
        CreateMod("30-b", "B", version: "1.0.0");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Empty(plan.LoadableMods);
        Assert.Equal("Cannot load 'A': conflicts with B < 2.0.0 (found B 1.0.0).", plan.BlockedMods.Single(mod => mod.Name == "A").Reason);
    }

    [Fact]
    public void Build_ConflictTriggersOnAMalformedInstalledMod()
    {
        CreateMod("10-a", "A", conflicts: ["B"]);
        CreateMod("20-b", "B", conflicts: [""]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Empty(plan.LoadableMods);
        Assert.Equal("Cannot load 'A': conflicts with B, which is installed.", plan.BlockedMods.Single(mod => mod.Name == "A").Reason);
    }

    [Fact]
    public void Build_DropsTheFewestOptionalEntriesThatLeaveNoCycle()
    {
        CreateMod("10-a", "A", optionalDepends: ["C"]);
        CreateMod("20-b", "B", depends: ["A", "C"]);
        CreateMod("30-c", "C", optionalDepends: ["B"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        // Dropping C's entry alone leaves no cycle. Dropping A's entry first would have
        // cost a second drop.
        Assert.Equal(["C", "A", "B"], Names(plan));
        Assert.Equal(
            ["'C' [30-c]: optional dependency 'B' is ignored for ordering, honouring it would form a cycle."],
            plan.Warnings);
    }

    [Fact]
    public void Build_DropsOneEntrySharedByTwoOptionalCycles()
    {
        CreateMod("10-a", "A", optionalDepends: ["B"]);
        CreateMod("20-b", "B", optionalDepends: ["C"]);
        CreateMod("30-c", "C", optionalDepends: ["A", "D"]);
        CreateMod("40-d", "D", optionalDepends: ["B"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        // B's entry for C sits on both cycles, so it is the only one dropped, and A still
        // loads after B.
        Assert.Equal(["B", "A", "D", "C"], Names(plan));
        Assert.Equal(
            ["'B' [20-b]: optional dependency 'C' is ignored for ordering, honouring it would form a cycle."],
            plan.Warnings);
    }

    [Fact]
    public void Build_BlocksBothCopiesWhenAMalformedManifestSharesAName()
    {
        CreateMod("10-x", "X", depends: [""]);
        CreateMod("20-x", "X");
        CreateMod("30-y", "Y", depends: ["X"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Empty(plan.LoadableMods);
        Assert.Equal(["X", "X", "Y"], plan.BlockedMods.Select(mod => mod.Name).ToArray());
        Assert.Equal("Manifest contains an empty dependency entry.", plan.BlockedMods[0].Reason);
        Assert.Equal("Duplicate manifest name 'X' also appears in: 10-x, 20-x.", plan.BlockedMods[1].Reason);
        Assert.Equal("Cannot load 'Y': requires X, which is blocked.", plan.BlockedMods[2].Reason);
    }

    [Theory]
    [InlineData("jiangyu", "jiangyu")]
    [InlineData("JIANGYU", "JIANGYU")]
    [InlineData("  Jiangyu  ", "Jiangyu")]
    public void Build_ReservesTheLoaderNameInAnyLetterCase(string name, string quoted)
    {
        CreateMod("10-impostor", name, version: "4.5.6");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Empty(plan.LoadableMods);
        var blocked = Assert.Single(plan.BlockedMods);
        Assert.Equal(string.Empty, blocked.Name);
        Assert.Equal("10-impostor", blocked.DisplayName);
        Assert.Equal($"Manifest name '{quoted}' is reserved for the loader.", blocked.Reason);
        Assert.Equal("4.5.6", blocked.Version);
    }

    [Fact]
    public void Build_PointsOutTheLoaderNameWrittenInAnotherCase()
    {
        CreateMod("10-a", "A", depends: ["jiangyu >= 1.0.0"]);
        CreateMod("20-b", "B", optionalDepends: ["JIANGYU"]);
        CreateMod("30-c", "C", conflicts: ["jiangyu >= 1.0.0"]);
        CreateMod("40-d", "D", depends: ["jiangyu-tools"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir, loaderVersion: "1.4.6");

        Assert.Equal(["B", "C"], Names(plan));
        Assert.Equal("Cannot load 'A': requires jiangyu >= 1.0.0, and the loader's name is Jiangyu.", plan.BlockedMods[0].Reason);
        Assert.Equal("Cannot load 'D': requires jiangyu-tools.", plan.BlockedMods[1].Reason);
        Assert.Equal(
            [
                "'B' [20-b]: optional dependency 'JIANGYU' is not the loader's name Jiangyu, the entry is ignored.",
                "'C' [30-c]: conflict 'jiangyu >= 1.0.0' is not the loader's name Jiangyu, the entry is ignored.",
            ],
            plan.Warnings);
    }

    [Fact]
    public void Build_RecoveredIdentityTakesTheLastKeyWhateverItsValue()
    {
        var modDir = Path.Combine(_modsDir, "10-b");
        Directory.CreateDirectory(modDir);
        File.WriteAllText(Path.Combine(modDir, "jiangyu.json"), "{ \"name\": \"B\", \"version\": \"1.0.0\", \"VERSION\": null, \"optionalDepends\": \"Ghost\" }");
        CreateMod("20-a", "A", conflicts: ["B < 2.0.0"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["A"], Names(plan));
        var blocked = Assert.Single(plan.BlockedMods);
        Assert.Equal("B", blocked.Name);
        Assert.Null(blocked.Version);
    }

    [Fact]
    public void Build_RecoversIdentityFromKeysInAnyLetterCase()
    {
        var modDir = Path.Combine(_modsDir, "10-bad");
        Directory.CreateDirectory(modDir);
        File.WriteAllText(Path.Combine(modDir, "jiangyu.json"), "{ \"Name\": \"A\", \"Version\": \"2.0.0\", \"depends\": \"NotAList\" }");
        CreateMod("20-other", "B", conflicts: ["A >= 2.0.0"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Empty(plan.LoadableMods);
        Assert.Equal("A", plan.BlockedMods[0].Name);
        Assert.Equal("2.0.0", plan.BlockedMods[0].Version);
        Assert.Equal("Cannot load 'B': conflicts with A >= 2.0.0 (found A 2.0.0).", plan.BlockedMods[1].Reason);
    }

    [Fact]
    public void Build_BlocksABlankNameAndShowsTheFolderInstead()
    {
        var modDir = Path.Combine(_modsDir, "10-nameless");
        Directory.CreateDirectory(modDir);
        File.WriteAllText(Path.Combine(modDir, "jiangyu.json"), "{ \"name\": \"   \" }");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Empty(plan.LoadableMods);
        var blocked = Assert.Single(plan.BlockedMods);
        Assert.Equal(string.Empty, blocked.Name);
        Assert.Equal("10-nameless", blocked.DisplayName);
        Assert.Equal("Manifest is missing a non-empty 'name'.", blocked.Reason);
    }

    [Fact]
    public void Build_TrimsTheNameOfAMalformedManifestForDependents()
    {
        CreateMod("10-addon", "Addon", depends: ["Base"], optionalDepends: ["Base"]);
        CreateMod("20-base", " Base ", conflicts: [""]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Empty(plan.LoadableMods);
        Assert.Equal("Cannot load 'Addon': requires Base, which is blocked.", plan.BlockedMods.Single(mod => mod.Name == "Addon").Reason);
    }

    [Fact]
    public void Build_BlocksAnEmptyOptionalDependencyEntry()
    {
        CreateMod("10-addon", "Addon", optionalDepends: [""]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Empty(plan.LoadableMods);
        Assert.Equal("Manifest contains an empty optional dependency entry.", Assert.Single(plan.BlockedMods).Reason);
    }

    [Fact]
    public void Build_ABareOptionalLoaderEntryNeitherOrdersNorLogs()
    {
        CreateMod("10-addon", "Addon", optionalDepends: ["Jiangyu"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir, loaderVersion: "1.4.6");

        Assert.Equal(["Addon"], Names(plan));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Build_BlockedOptionalWarningNamesEachEntry()
    {
        CreateMod("10-addon", "Addon", optionalDepends: ["Base", "Base >= 2.0.0"]);
        CreateMod("20-base", "Base", depends: ["Ghost"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["Addon"], Names(plan));
        Assert.Equal(
            [
                "'Addon' [10-addon]: optional dependency 'Base' is installed but blocked, the entry is ignored.",
                "'Addon' [10-addon]: optional dependency 'Base >= 2.0.0' is installed but blocked, the entry is ignored.",
            ],
            plan.Warnings);
    }

    [Fact]
    public void Build_WarnsOncePerSelfNamingEntry()
    {
        CreateMod("10-a", "A", depends: ["A", "A >= 1.0.0"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["A"], Names(plan));
        Assert.Equal(
            [
                "'A' [10-a]: dependency 'A' names the mod itself, the entry is ignored.",
                "'A' [10-a]: dependency 'A >= 1.0.0' names the mod itself, the entry is ignored.",
            ],
            plan.Warnings);
    }

    [Fact]
    public void Build_AManifestOfTheWrongShapeStillCountsAsInstalled()
    {
        var modDir = Path.Combine(_modsDir, "10-b");
        Directory.CreateDirectory(modDir);
        File.WriteAllText(Path.Combine(modDir, "jiangyu.json"), "{ \"name\": \" B \", \"version\": \"1.0.0\", \"optionalDepends\": \"Ghost\" }");
        CreateMod("20-b", "B", version: "3.0.0");
        CreateMod("30-a", "A", conflicts: ["B < 2.0.0"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Empty(plan.LoadableMods);
        Assert.Equal(["B", "B", "A"], plan.BlockedMods.Select(mod => mod.Name).ToArray());
        Assert.StartsWith("Failed to read manifest:", plan.BlockedMods[0].Reason);
        Assert.Equal("1.0.0", plan.BlockedMods[0].Version);
        Assert.Equal("Duplicate manifest name 'B' also appears in: 10-b, 20-b.", plan.BlockedMods[1].Reason);
        Assert.Equal("Cannot load 'A': conflicts with B < 2.0.0 (found B 1.0.0).", plan.BlockedMods[2].Reason);
    }

    [Fact]
    public void Build_AnUnreadableManifestNamedAfterTheLoaderStaysAnonymous()
    {
        var modDir = Path.Combine(_modsDir, "10-impostor");
        Directory.CreateDirectory(modDir);
        File.WriteAllText(Path.Combine(modDir, "jiangyu.json"), "{ \"name\": \"Jiangyu\", \"version\": \"9.9.9\", \"depends\": 5 }");
        CreateMod("20-a", "A", conflicts: ["Jiangyu >= 9.0.0"]);

        var plan = ModLoadPlanBuilder.Build(_modsDir, loaderVersion: "1.4.6");

        Assert.Equal(["A"], Names(plan));
        var blocked = Assert.Single(plan.BlockedMods);
        Assert.Equal(string.Empty, blocked.Name);
        Assert.Equal("10-impostor", blocked.DisplayName);
    }

    [Fact]
    public void Build_UnreadableInstalledVersionDegradesRequiredAndOptionalToPresence()
    {
        CreateMod("10-addon", "Addon", depends: ["Base >= 9.0.0"], optionalDepends: ["Other >= 9.0.0"]);
        CreateMod("20-base", "Base", version: "nightly");
        CreateMod("30-other", "Other", version: "nightly");

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["Base", "Other", "Addon"], Names(plan));
        Assert.Empty(plan.BlockedMods);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Build_RangedConflictDoesNotTriggerOnACopyWithoutAReadableVersion()
    {
        CreateMod("10-a", "A", conflicts: ["B < 2.0.0"]);
        CreateMod("20-b", "B", conflicts: [""]);

        var plan = ModLoadPlanBuilder.Build(_modsDir);

        Assert.Equal(["A"], Names(plan));
        Assert.Equal("B", Assert.Single(plan.BlockedMods).Name);
    }

    [Fact]
    public void Build_ADenseOptionalTangleSettlesInOnePassToFolderOrder()
    {
        const int count = 30;
        var names = Enumerable.Range(0, count).Select(index => $"M{index:00}").ToArray();
        foreach (var (name, index) in names.Select((name, index) => (name, index)))
            CreateMod($"{index:00}-{name.ToLowerInvariant()}", name, optionalDepends: names.Where(other => other != name).ToArray());

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var plan = ModLoadPlanBuilder.Build(_modsDir);
        watch.Stop();

        // Every entry pointing at an earlier folder is honoured, every entry pointing at a
        // later one is ignored, and the plan lands well inside a boot budget.
        Assert.Equal(names, Names(plan));
        Assert.Equal(count * (count - 1) / 2, plan.Warnings.Count);
        Assert.All(plan.Warnings, warning => Assert.EndsWith("would form a cycle.", warning));
        Assert.True(watch.ElapsedMilliseconds < 2000, $"took {watch.ElapsedMilliseconds} ms");
    }

    private static string[] Names(ModLoadPlan plan) => plan.LoadableMods.Select(mod => mod.Name).ToArray();

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
        string[]? conflicts = null,
        string[]? optionalDepends = null)
    {
        var modDir = Path.Combine(_modsDir, relativeDir);
        Directory.CreateDirectory(modDir);
        File.WriteAllText(
            Path.Combine(modDir, "jiangyu.json"),
            BuildManifestJson(name, depends, version, conflicts, optionalDepends));
        var bundlesDir = Path.Combine(modDir, "bundles");
        Directory.CreateDirectory(bundlesDir);
        File.WriteAllText(Path.Combine(bundlesDir, $"{name}.bundle"), "bundle");
    }

    private static string BuildManifestJson(string name, string[]? depends, string? version = null, string[]? conflicts = null, string[]? optionalDepends = null)
    {
        var fields = new List<string> { $"\"name\": \"{name}\"" };
        if (version != null)
            fields.Add($"\"version\": \"{version}\"");
        if (depends is { Length: > 0 })
            fields.Add($"\"depends\": [{string.Join(", ", depends.Select(dep => $"\"{dep}\""))}]");
        if (optionalDepends is { Length: > 0 })
            fields.Add($"\"optionalDepends\": [{string.Join(", ", optionalDepends.Select(dep => $"\"{dep}\""))}]");
        if (conflicts is { Length: > 0 })
            fields.Add($"\"conflicts\": [{string.Join(", ", conflicts.Select(con => $"\"{con}\""))}]");
        return $"{{\n  {string.Join(",\n  ", fields)}\n}}";
    }
}
