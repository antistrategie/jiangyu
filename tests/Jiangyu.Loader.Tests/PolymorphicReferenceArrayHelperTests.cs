using Jiangyu.Loader.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Tests the structural-decision helpers used by the clone applier and
/// patch applier to identify polymorphic-reference arrays.
///
/// Test inputs are synthetic fixture types declared in
/// <c>PolymorphicReferenceArrayHelperFixtures.cs</c> — plain managed
/// C# classes that descend only from <see cref="object"/>, never from
/// <c>UnityEngine.*</c> or <c>Il2Cpp*</c> types. This keeps the tests
/// independent of the stripped Il2Cppmscorlib / UnityEngine.CoreModule
/// shipped in CI, which fail .NET 10 type loading in different ways.
/// The structural rules under test (abstract polymorphic with subclasses
/// → owned; concrete → not owned; descendant of DataTemplate → not
/// owned) work on any type lattice; we use synthetic bases to drive them.
///
/// <see cref="TemplateCloneApplier.IsOwnedElementType"/> takes the
/// real game types via <c>typeof()</c> at JIT time (production); the
/// internal <c>IsOwnedElementTypeCore</c> overload accepts them as
/// parameters so tests can pass synthetic bases.
/// </summary>
public class PolymorphicReferenceArrayHelperTests
{
    // --- IsOwnedElementTypeCore (parameterised; synthetic bases) ---

    [Fact]
    public void IsOwnedElementType_AbstractPolymorphicSO_IsOwned()
    {
        Assert.True(TemplateCloneApplier.IsOwnedElementTypeCore(
            typeof(TestPolymorphicHandlerBase),
            dataTemplateBase: typeof(TestDataTemplateBase),
            scriptableObjectBase: typeof(TestScriptableObjectBase)));
    }

    [Fact]
    public void IsOwnedElementType_ConcreteSO_IsNotOwned()
    {
        // Concrete ScriptableObject with no subtypes — the SkillGroup /
        // DefectGroup shape. Cloning a parent must NOT deep-copy these:
        // they're shared registry wrappers.
        Assert.False(TemplateCloneApplier.IsOwnedElementTypeCore(
            typeof(TestConcreteWrapper),
            dataTemplateBase: typeof(TestDataTemplateBase),
            scriptableObjectBase: typeof(TestScriptableObjectBase)));
    }

    [Fact]
    public void IsOwnedElementType_DataTemplateDescendant_IsNotOwned()
    {
        // DataTemplate descendants carry m_ID and live in the registry —
        // intentional sharing, never deep-copy on parent clone.
        Assert.False(TemplateCloneApplier.IsOwnedElementTypeCore(
            typeof(TestDataTemplateLike),
            dataTemplateBase: typeof(TestDataTemplateBase),
            scriptableObjectBase: typeof(TestScriptableObjectBase)));
    }

    [Fact]
    public void IsOwnedElementType_NonScriptableObject_IsNotOwned()
    {
        // Plain managed types and value types are never owned — the rule
        // is specifically about ScriptableObject elements.
        Assert.False(TemplateCloneApplier.IsOwnedElementTypeCore(
            typeof(string), typeof(TestDataTemplateBase), typeof(TestScriptableObjectBase)));
        Assert.False(TemplateCloneApplier.IsOwnedElementTypeCore(
            typeof(int), typeof(TestDataTemplateBase), typeof(TestScriptableObjectBase)));
        Assert.False(TemplateCloneApplier.IsOwnedElementTypeCore(
            typeof(System.IDisposable), typeof(TestDataTemplateBase), typeof(TestScriptableObjectBase)));
    }

    [Fact]
    public void IsOwnedElementType_ConcreteSubtypeOfPolymorphic_IsNotOwnedItself()
    {
        // The concrete subclass itself isn't owned — the rule applies to
        // the abstract base. (When applied: a List<TestPolymorphicHandlerBase>
        // is detected as owned via the base; concrete leaves never appear
        // as the declared element type of an array in production.)
        Assert.False(TemplateCloneApplier.IsOwnedElementTypeCore(
            typeof(TestConcreteHandlerA),
            dataTemplateBase: typeof(TestDataTemplateBase),
            scriptableObjectBase: typeof(TestScriptableObjectBase)));
    }

    // (Tests for null base parameters intentionally omitted: the
    // OwnedElementTypeCache is keyed by elementType alone, which is
    // correct in production where the bases are always typeof()
    // constants. Test-only assertions with different bases against the
    // same elementType would show stale cache hits — a test artefact,
    // not a production bug.)

    // --- HasStrictDescendant ---

    [Fact]
    public void HasStrictDescendant_AbstractBaseWithSubtypes_True()
    {
        Assert.True(TemplateCloneApplier.HasStrictDescendant(typeof(TestPolymorphicHandlerBase)));
    }

    [Fact]
    public void HasStrictDescendant_ConcreteWithoutSubtypes_False()
    {
        Assert.False(TemplateCloneApplier.HasStrictDescendant(typeof(TestConcreteWrapper)));
    }

    [Fact]
    public void HasStrictDescendant_BaseExcludesItself()
    {
        // ReferenceEquals self-check ensures the method doesn't
        // pathologically return true on every type.
        Assert.False(TemplateCloneApplier.HasStrictDescendant(typeof(TestConcreteHandlerA)));
    }

    // --- Il2CppCollectionReflection.GetListElementType ---

    [Fact]
    public void GetListElementType_BclList_UnwrapsToElement()
    {
        Assert.Equal(
            typeof(string),
            Il2CppCollectionReflection.GetListElementType(typeof(System.Collections.Generic.List<string>)));
    }

    [Fact]
    public void GetListElementType_NonListGeneric_ReturnsNull()
    {
        Assert.Null(Il2CppCollectionReflection.GetListElementType(
            typeof(System.Collections.Generic.HashSet<string>)));
        Assert.Null(Il2CppCollectionReflection.GetListElementType(typeof(string[])));
        Assert.Null(Il2CppCollectionReflection.GetListElementType(typeof(int)));
        Assert.Null(Il2CppCollectionReflection.GetListElementType(typeof(string)));
    }

    [Fact]
    public void GetListElementType_NullInput_ReturnsNull()
    {
        Assert.Null(Il2CppCollectionReflection.GetListElementType(null));
    }

    // --- Il2CppCollectionReflection.GetArrayElementType ---

    [Fact]
    public void GetArrayElementType_ManagedArray_ReturnsElement()
    {
        Assert.Equal(typeof(string),
            Il2CppCollectionReflection.GetArrayElementType(typeof(string[])));
        Assert.Equal(typeof(int),
            Il2CppCollectionReflection.GetArrayElementType(typeof(int[])));
    }

    [Fact]
    public void GetArrayElementType_NonArrayInput_ReturnsNull()
    {
        Assert.Null(Il2CppCollectionReflection.GetArrayElementType(
            typeof(System.Collections.Generic.List<string>)));
        Assert.Null(Il2CppCollectionReflection.GetArrayElementType(typeof(int)));
        Assert.Null(Il2CppCollectionReflection.GetArrayElementType(null));
    }

    // --- Il2CppCollectionReflection.TryRebuildList ---
    //
    // Live IL2CPP List wrappers can't instantiate in CI, but the BCL
    // List<T> code path goes through the same Activator / Count / Item /
    // Add reflection ceremony, so a BCL-list round-trip pins the
    // semantics: same elements, new container.

    [Fact]
    public void TryRebuildList_BclListString_CopiesElementsIntoFreshContainer()
    {
        var source = new System.Collections.Generic.List<string> { "a", "b", "c" };
        var ok = Il2CppCollectionReflection.TryRebuildList(
            source, typeof(System.Collections.Generic.List<string>), typeof(string),
            out var fresh, out var error);

        Assert.True(ok, error);
        Assert.NotNull(fresh);
        Assert.NotSame(source, fresh);
        var freshList = Assert.IsType<System.Collections.Generic.List<string>>(fresh);
        Assert.Equal(new[] { "a", "b", "c" }, freshList);

        // Container is independent: mutating the source's list after
        // rebuild does not affect the fresh one.
        source.Add("d");
        Assert.Equal(3, freshList.Count);
    }

    [Fact]
    public void TryRebuildList_EmptySource_ReturnsEmptyFresh()
    {
        var source = new System.Collections.Generic.List<int>();
        var ok = Il2CppCollectionReflection.TryRebuildList(
            source, typeof(System.Collections.Generic.List<int>), typeof(int),
            out var fresh, out var error);

        Assert.True(ok, error);
        Assert.NotSame(source, fresh);
        Assert.Empty((System.Collections.Generic.List<int>)fresh);
    }

    [Fact]
    public void TryRebuildList_NullSource_ReturnsFailure()
    {
        var ok = Il2CppCollectionReflection.TryRebuildList(
            null, typeof(System.Collections.Generic.List<string>), typeof(string),
            out var fresh, out var error);

        Assert.False(ok);
        Assert.Null(fresh);
        Assert.NotNull(error);
    }

    // --- Il2CppCollectionReflection.TryCreateEmptyList ---

    [Fact]
    public void TryCreateEmptyList_BclList_ReturnsEmptyInstance()
    {
        var ok = Il2CppCollectionReflection.TryCreateEmptyList(
            typeof(System.Collections.Generic.List<string>), out var fresh, out var error);

        Assert.True(ok, error);
        Assert.NotNull(fresh);
        Assert.Empty((System.Collections.Generic.List<string>)fresh);
    }

    // --- TemplateCloneApplier.CloneValueObjectByFieldReflection ---
    //
    // The production usage hands in IL2CPP wrapper types; the helper falls
    // back to Activator.CreateInstance for plain managed types (the
    // typeof(Il2CppObjectBase).IsAssignableFrom check fails for non-IL2CPP
    // fixtures, so it takes the managed-Activator branch). That branch is
    // testable here because the rest of the helper — property/field
    // enumeration, List<string> reseat — is shape-agnostic.

    [Fact]
    public void CloneValueObjectByFieldReflection_CopiesScalarFields()
    {
        var src = new TestRoleLike
        {
            Tag = 42,
            Name = "click_bark",
            SerialisedRequirements = new List<string> { "sy" },
            NestedShared = new TestRoleNestedValue { Marker = 7 },
        };
        var clone = TemplateCloneApplier.CloneValueObjectByFieldReflection(
            src, typeof(TestRoleLike), "test", log: null);

        var fresh = Assert.IsType<TestRoleLike>(clone);
        Assert.NotSame(src, fresh);
        Assert.Equal(42, fresh.Tag);
        Assert.Equal("click_bark", fresh.Name);
    }

    [Fact]
    public void CloneValueObjectByFieldReflection_ReseatsListOfString()
    {
        var src = new TestRoleLike
        {
            SerialisedRequirements = new List<string> { "sy" },
        };
        var fresh = (TestRoleLike)TemplateCloneApplier.CloneValueObjectByFieldReflection(
            src, typeof(TestRoleLike), "test", log: null);

        // List<string> field is reallocated, so per-index modder edits on
        // the clone don't leak into the source. String contents stay
        // shared at the element level.
        Assert.NotSame(src.SerialisedRequirements, fresh.SerialisedRequirements);
        Assert.Equal(new[] { "sy" }, fresh.SerialisedRequirements);

        fresh.SerialisedRequirements[0] = "voymastina";
        Assert.Equal("sy", src.SerialisedRequirements[0]);
    }

    [Fact]
    public void CloneValueObjectByFieldReflection_KeepsNonStringReferenceFieldsShared()
    {
        // Non-string-list reference fields keep their ref shared. Today
        // those are immutable strings or external assets that aren't
        // mutated by patches; deep-copying them risks breaking
        // intentional sharing on unrelated types.
        var nested = new TestRoleNestedValue { Marker = 9 };
        var src = new TestRoleLike { NestedShared = nested };

        var fresh = (TestRoleLike)TemplateCloneApplier.CloneValueObjectByFieldReflection(
            src, typeof(TestRoleLike), "test", log: null);

        Assert.Same(nested, fresh.NestedShared);
    }

    [Fact]
    public void CloneValueObjectByFieldReflection_NullSource_ReturnsNull()
    {
        Assert.Null(TemplateCloneApplier.CloneValueObjectByFieldReflection(
            null, typeof(TestRoleLike), "test", log: null));
    }

    // --- Il2CppCollectionReflection.TryCreateEmptyArray ---

    [Fact]
    public void TryCreateEmptyArray_ManagedArray_ReturnsZeroLengthArray()
    {
        var ok = Il2CppCollectionReflection.TryCreateEmptyArray(
            typeof(string[]), typeof(string), out var fresh, out var error);

        Assert.True(ok, error);
        var arr = Assert.IsType<string[]>(fresh);
        Assert.Empty(arr);
    }

    // --- ResolveIl2CppSubtype ---

    [Fact]
    public void ResolveIl2CppSubtype_SubtypeInSameAssembly_Resolves()
    {
        var resolved = TemplatePatchApplier.ResolveIl2CppSubtype(
            typeof(TestPolymorphicHandlerBase),
            "TestConcreteHandlerA");

        Assert.NotNull(resolved);
        Assert.Equal("TestConcreteHandlerA", resolved!.Name);
        Assert.True(typeof(TestPolymorphicHandlerBase).IsAssignableFrom(resolved));
    }

    [Fact]
    public void ResolveIl2CppSubtype_NonSubtype_NotResolvable()
    {
        // TestConcreteWrapper is in the same namespace but isn't a
        // TestPolymorphicHandlerBase. Same-namespace fast path must
        // still check assignability; otherwise the caller sees a
        // misleading "type does not derive" error downstream rather
        // than a clean "no such subtype" error here.
        var resolved = TemplatePatchApplier.ResolveIl2CppSubtype(
            typeof(TestPolymorphicHandlerBase),
            "TestConcreteWrapper");

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveIl2CppSubtype_UnknownName_ReturnsNull()
    {
        var resolved = TemplatePatchApplier.ResolveIl2CppSubtype(
            typeof(TestPolymorphicHandlerBase),
            "DefinitelyNotAType_xyzzy");

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveIl2CppSubtype_CachesResults()
    {
        var first = TemplatePatchApplier.ResolveIl2CppSubtype(
            typeof(TestPolymorphicHandlerBase),
            "TestConcreteHandlerB");
        var second = TemplatePatchApplier.ResolveIl2CppSubtype(
            typeof(TestPolymorphicHandlerBase),
            "TestConcreteHandlerB");

        Assert.Same(first, second);
    }

    [Fact]
    public void ResolveIl2CppSubtype_CachedNullStays_Null()
    {
        var first = TemplatePatchApplier.ResolveIl2CppSubtype(
            typeof(TestPolymorphicHandlerBase),
            "AnotherImpossibleName_xyzzy");
        var second = TemplatePatchApplier.ResolveIl2CppSubtype(
            typeof(TestPolymorphicHandlerBase),
            "AnotherImpossibleName_xyzzy");

        Assert.Null(first);
        Assert.Null(second);
    }
}
