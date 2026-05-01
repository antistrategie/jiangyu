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

    // --- GetIl2CppListElementType ---

    [Fact]
    public void GetIl2CppListElementType_BclList_UnwrapsToElement()
    {
        Assert.Equal(
            typeof(string),
            TemplateCloneApplier.GetIl2CppListElementType(typeof(System.Collections.Generic.List<string>)));
    }

    [Fact]
    public void GetIl2CppListElementType_NonListGeneric_ReturnsNull()
    {
        Assert.Null(TemplateCloneApplier.GetIl2CppListElementType(
            typeof(System.Collections.Generic.HashSet<string>)));
        Assert.Null(TemplateCloneApplier.GetIl2CppListElementType(typeof(string[])));
        Assert.Null(TemplateCloneApplier.GetIl2CppListElementType(typeof(int)));
        Assert.Null(TemplateCloneApplier.GetIl2CppListElementType(typeof(string)));
    }

    [Fact]
    public void GetIl2CppListElementType_NullInput_ReturnsNull()
    {
        Assert.Null(TemplateCloneApplier.GetIl2CppListElementType(null));
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
