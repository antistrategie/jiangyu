using Jiangyu.Loader.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Tests the structural-decision helpers used by the clone applier and
/// patch applier to identify polymorphic-reference arrays. These helpers
/// are pure managed reflection — they walk <see cref="Type"/> and call
/// <c>Assembly.GetTypes()</c> without invoking the IL2CPP runtime — so
/// they can run in a vanilla .NET test host as long as the stripped
/// Assembly-CSharp.dll is on the test runtime path (which the project
/// reference to <c>Jiangyu.Loader</c> arranges).
///
/// These tests are load-bearing: they assert the rule that decides which
/// fields get deep-copied on clone (owned vs shared) and which subtypes
/// resolve from a <c>type=</c>/<c>handler=</c> short name. A regression
/// here would silently change clone semantics across the whole game.
/// </summary>
public class PolymorphicReferenceArrayHelperTests
{
    // --- IsOwnedElementType ---

    [Fact]
    public void IsOwnedElementType_SkillEventHandlerTemplate_IsOwned()
    {
        // Abstract SerializedScriptableObject base with 119+ subclasses;
        // the canonical "owned by parent" shape that drives clone-time
        // deep-copy of EventHandlers lists.
        Assert.True(TemplateCloneApplier.IsOwnedElementType(
            typeof(Il2CppMenace.Tactical.Skills.SkillEventHandlerTemplate)));
    }

    [Fact]
    public void IsOwnedElementType_SkillGroup_IsNotOwned()
    {
        // ScriptableObject-derived but concrete (no subtypes) and
        // semantically a shared registry wrapper. Cloning a parent must
        // NOT deep-copy SkillGroups — they're referenced by many entities.
        Assert.False(TemplateCloneApplier.IsOwnedElementType(
            typeof(Il2CppMenace.Tactical.Skills.SkillGroup)));
    }

    [Fact]
    public void IsOwnedElementType_DataTemplate_IsNotOwned()
    {
        // DataTemplate descendants carry m_ID and live in the registry —
        // intentional sharing, never deep-copy on parent clone.
        Assert.False(TemplateCloneApplier.IsOwnedElementType(
            typeof(Il2CppMenace.Tactical.Skills.SkillTemplate)));
        Assert.False(TemplateCloneApplier.IsOwnedElementType(
            typeof(Il2CppMenace.Tactical.EntityTemplate)));
    }

    [Fact]
    public void IsOwnedElementType_NonScriptableObject_IsNotOwned()
    {
        // Plain managed types and value types are never owned — the rule
        // is specifically about ScriptableObject elements.
        Assert.False(TemplateCloneApplier.IsOwnedElementType(typeof(string)));
        Assert.False(TemplateCloneApplier.IsOwnedElementType(typeof(int)));
        Assert.False(TemplateCloneApplier.IsOwnedElementType(typeof(System.IDisposable)));
    }

    // --- HasStrictDescendant ---

    [Fact]
    public void HasStrictDescendant_AbstractBaseWithSubtypes_True()
    {
        // SkillEventHandlerTemplate has 119+ concrete subclasses in
        // Assembly-CSharp; presence is the structural signal that this
        // is a polymorphic-abstract shape.
        Assert.True(TemplateCloneApplier.HasStrictDescendant(
            typeof(Il2CppMenace.Tactical.Skills.SkillEventHandlerTemplate)));
    }

    [Fact]
    public void HasStrictDescendant_ConcreteWithoutSubtypes_False()
    {
        // SkillGroup is a concrete wrapper with no subclasses anywhere
        // in the assembly; the structural rule must distinguish it from
        // abstract polymorphic bases like SkillEventHandlerTemplate.
        Assert.False(TemplateCloneApplier.HasStrictDescendant(
            typeof(Il2CppMenace.Tactical.Skills.SkillGroup)));
    }

    [Fact]
    public void HasStrictDescendant_BaseExcludesItself()
    {
        // The "strict descendant" predicate returns true only when at
        // least one OTHER type derives from the input — passing a leaf
        // type can't return true via self-match. Object descends from
        // every type but doesn't trigger HasStrictDescendant against
        // itself because the helper uses ReferenceEquals to skip self.
        // (Ensures the same-type loop never falls into infinite-true.)
        Assert.False(TemplateCloneApplier.HasStrictDescendant(typeof(string)));
    }

    // --- GetIl2CppListElementType ---

    [Fact]
    public void GetIl2CppListElementType_Il2CppList_UnwrapsToElement()
    {
        // The wire format that real game data exposes for EventHandlers,
        // Skills, Items, etc. — the Il2CppInterop generated wrapper for
        // List<T>. The helper must recognise and unwrap to T.
        var listOfHandlers = typeof(Il2CppSystem.Collections.Generic.List<>)
            .MakeGenericType(typeof(Il2CppMenace.Tactical.Skills.SkillEventHandlerTemplate));

        var elementType = TemplateCloneApplier.GetIl2CppListElementType(listOfHandlers);

        Assert.Equal(typeof(Il2CppMenace.Tactical.Skills.SkillEventHandlerTemplate), elementType);
    }

    [Fact]
    public void GetIl2CppListElementType_BclList_UnwrapsToElement()
    {
        // Plain .NET List<T> (used in Jiangyu.Shared types) also unwraps.
        var listOfStrings = typeof(System.Collections.Generic.List<string>);

        var elementType = TemplateCloneApplier.GetIl2CppListElementType(listOfStrings);

        Assert.Equal(typeof(string), elementType);
    }

    [Fact]
    public void GetIl2CppListElementType_NonListGeneric_ReturnsNull()
    {
        // Other generic collection types (HashSet, Dictionary, etc.) and
        // arrays don't match the list shape — the deep-copy walk skips
        // them. Returning null is the structural skip signal.
        Assert.Null(TemplateCloneApplier.GetIl2CppListElementType(
            typeof(System.Collections.Generic.HashSet<string>)));
        Assert.Null(TemplateCloneApplier.GetIl2CppListElementType(typeof(string[])));
        Assert.Null(TemplateCloneApplier.GetIl2CppListElementType(typeof(int)));
    }

    // --- ResolveIl2CppSubtype ---

    [Fact]
    public void ResolveIl2CppSubtype_SubtypeInSameNamespace_Resolves()
    {
        // The common case: AddSkill lives in the same namespace as its
        // base SkillEventHandlerTemplate. Same-namespace lookup is the
        // fast path; fall-through to global search isn't needed.
        var resolved = TemplatePatchApplier.ResolveIl2CppSubtype(
            typeof(Il2CppMenace.Tactical.Skills.SkillEventHandlerTemplate),
            "AddSkill");

        Assert.NotNull(resolved);
        Assert.Equal("AddSkill", resolved!.Name);
        Assert.True(typeof(Il2CppMenace.Tactical.Skills.SkillEventHandlerTemplate).IsAssignableFrom(resolved));
    }

    [Fact]
    public void ResolveIl2CppSubtype_NonSubtype_NotResolvable()
    {
        // SkillGroup isn't a SkillEventHandlerTemplate. The fall-through
        // global search filters by IsAssignableFrom, so it shouldn't
        // match. Returns null so callers can produce a clean error.
        var resolved = TemplatePatchApplier.ResolveIl2CppSubtype(
            typeof(Il2CppMenace.Tactical.Skills.SkillEventHandlerTemplate),
            "SkillGroup");

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveIl2CppSubtype_UnknownName_ReturnsNull()
    {
        var resolved = TemplatePatchApplier.ResolveIl2CppSubtype(
            typeof(Il2CppMenace.Tactical.Skills.SkillEventHandlerTemplate),
            "DefinitelyNotAType_xyzzy");

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveIl2CppSubtype_CachesResults()
    {
        // The cache is structural: same (namespace, shortName) key returns
        // the same Type reference. Assert reference equality across two
        // calls so a regression that bypasses the cache (e.g. a refactor
        // that drops the dictionary lookup) is caught — repeated lookups
        // would otherwise produce equal but distinct Type objects.
        var first = TemplatePatchApplier.ResolveIl2CppSubtype(
            typeof(Il2CppMenace.Tactical.Skills.SkillEventHandlerTemplate),
            "AddSkill");
        var second = TemplatePatchApplier.ResolveIl2CppSubtype(
            typeof(Il2CppMenace.Tactical.Skills.SkillEventHandlerTemplate),
            "AddSkill");

        Assert.Same(first, second);
    }

    [Fact]
    public void ResolveIl2CppSubtype_CachedNullStays_Null()
    {
        // Caching the null result is important too: the global-fallback
        // search is expensive, and a "no such type" answer should be
        // remembered so we don't repeat the work each time the loader
        // hits the same bad hint.
        var first = TemplatePatchApplier.ResolveIl2CppSubtype(
            typeof(Il2CppMenace.Tactical.Skills.SkillEventHandlerTemplate),
            "AnotherImpossibleName_xyzzy");
        var second = TemplatePatchApplier.ResolveIl2CppSubtype(
            typeof(Il2CppMenace.Tactical.Skills.SkillEventHandlerTemplate),
            "AnotherImpossibleName_xyzzy");

        Assert.Null(first);
        Assert.Null(second);
    }
}
