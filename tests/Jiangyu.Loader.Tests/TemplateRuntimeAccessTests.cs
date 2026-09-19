using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jiangyu.Loader.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

public sealed class TemplateRuntimeAccessTests
{
    private static bool Assignable(Type target, Type candidate) => target.IsAssignableFrom(candidate);
    private static bool NoneFromGame(Type candidate) => false;

    [Fact]
    public void NarrowByAssignability_PicksTheOneCandidateTheTargetAccepts()
    {
        var narrowed = TemplateRuntimeAccess.NarrowByAssignability(
            [typeof(string), typeof(List<int>)], typeof(IEnumerable<int>), Assignable, NoneFromGame, out var competing);

        Assert.Equal(typeof(List<int>), narrowed);
        Assert.Equal([typeof(List<int>)], competing);
    }

    [Fact]
    public void NarrowByAssignability_NamesTheCandidatesWhenSeveralFit()
    {
        var narrowed = TemplateRuntimeAccess.NarrowByAssignability(
            [typeof(List<int>), typeof(int[])], typeof(IEnumerable<int>), Assignable, NoneFromGame, out var competing);

        Assert.Null(narrowed);
        Assert.Equal([typeof(List<int>), typeof(int[])], competing);
    }

    [Fact]
    public void NarrowByAssignability_GivesNothingWhenNoCandidateFits()
    {
        var narrowed = TemplateRuntimeAccess.NarrowByAssignability(
            [typeof(string), typeof(int)], typeof(IEnumerable<int>), Assignable, NoneFromGame, out var competing);

        Assert.Null(narrowed);
        Assert.Empty(competing);
    }

    [Fact]
    public void NarrowByAssignability_PrefersTheGamesOwnCandidate()
    {
        // A mod class that fits the destination cannot make the game's own twin ambiguous.
        var narrowed = TemplateRuntimeAccess.NarrowByAssignability(
            [typeof(List<int>), typeof(int[])], typeof(IEnumerable<int>), Assignable, candidate => candidate == typeof(int[]), out var competing);

        Assert.Equal(typeof(int[]), narrowed);
        Assert.Equal([typeof(int[])], competing);
    }
}

// The game's interop assemblies are not copied beside the tests. This class loads them
// from the install the loader was built against (the build's GameAssembliesDir, stamped
// into the test assembly) and stands down as Skipped when that install is absent. It
// runs alone, since it installs a resolve hook and loads the game's assembly into the
// test process.
[CollectionDefinition("game assemblies", DisableParallelization = true)]
public sealed class GameAssembliesCollection { }

[Collection("game assemblies")]
public sealed class TemplateRuntimeAccessGameTests
{
    private static readonly string GameAssembliesDir = typeof(TemplateRuntimeAccessGameTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "GameAssembliesDir")?.Value ?? string.Empty;

    private static readonly string MelonLoaderDir = typeof(TemplateRuntimeAccessGameTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "MelonLoaderDir")?.Value ?? string.Empty;

    private static Assembly? LoadFromGame(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name + ".dll";
        foreach (var dir in new[] { GameAssembliesDir, MelonLoaderDir })
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path))
                return Assembly.LoadFrom(path);
        }
        return null;
    }

    // The game's interop assembly holds Attack twice: the skill effect (a
    // SkillEventHandlerTemplate) and the AI behaviour. The short name alone is
    // ambiguous, and the destination it is written to settles it.
    [SkippableFact]
    public void ResolveTemplateType_SettlesATwinByTheDestination()
    {
        // A full install holds MelonLoader in the directory the build was pointed at and
        // the wrappers beside it. CI's stripped reference set holds wrappers that cannot
        // be loaded for execution, and its MelonLoader lives elsewhere, so it stands down.
        var gameAssembly = Path.Combine(GameAssembliesDir, "Assembly-CSharp.dll");
        var melonLoader = Path.Combine(MelonLoaderDir, "MelonLoader.dll");
        var sameInstall = !string.IsNullOrEmpty(MelonLoaderDir)
            && string.Equals(Path.GetFullPath(Path.Combine(MelonLoaderDir, "..")), Path.GetFullPath(Path.Combine(GameAssembliesDir, "..")), StringComparison.OrdinalIgnoreCase);
        Skip.If(!File.Exists(gameAssembly) || !File.Exists(melonLoader) || !sameInstall, $"no full game install at '{GameAssembliesDir}'");

        AppDomain.CurrentDomain.AssemblyResolve += LoadFromGame;
        try
        {
            var game = Assembly.LoadFrom(gameAssembly);
            var effect = game.GetType("Il2CppMenace.Tactical.Skills.Effects.Attack", throwOnError: true)!;
            var behaviour = game.GetType("Il2CppMenace.Tactical.AI.Behaviors.Attack", throwOnError: true)!;
            var handlerBase = game.GetType("Il2CppMenace.Tactical.Skills.SkillEventHandlerTemplate", throwOnError: true)!;

            var plain = TemplateRuntimeAccess.ResolveTemplateType("Attack", out var error);
            var narrowed = TemplateRuntimeAccess.ResolveTemplateType("Attack", handlerBase, out var narrowedError);
            var again = TemplateRuntimeAccess.ResolveTemplateType("Attack", handlerBase, out _);
            var exact = TemplateRuntimeAccess.ResolveTemplateType(behaviour.FullName!, handlerBase, out _);

            Assert.Null(plain);
            Assert.Contains("is ambiguous", error);
            Assert.Contains(effect.FullName!, error);
            Assert.Equal(effect, narrowed);
            Assert.Null(narrowedError);
            Assert.Equal(effect, again);
            // A full name that does not fit the destination comes back as itself, so the
            // caller reports the assignability failure against it.
            Assert.Equal(behaviour, exact);
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= LoadFromGame;
        }
    }
}
