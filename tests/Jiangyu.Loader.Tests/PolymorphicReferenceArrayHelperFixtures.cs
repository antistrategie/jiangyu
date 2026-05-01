// Synthetic fixture types used by PolymorphicReferenceArrayHelperTests.
// All types are plain managed C# (descend from object directly or via a
// stub base). Crucially, no fixture descends from UnityEngine.* or
// Il2Cpp* types — that would force Il2Cppmscorlib resolution at JIT
// time, which fails on the stripped Il2Cppmscorlib shipped in CI deps.
//
// The helper under test (TemplateCloneApplier.IsOwnedElementTypeCore) is
// parameterised over the DataTemplate and ScriptableObject base types,
// so tests pass these stubs and the helper's logic runs against them.
// The structural rule being tested is the same shape regardless of
// whether the input is a synthetic fixture or a real game type.

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Stub for the test-side ScriptableObject base. Tests pass
/// <c>typeof(TestScriptableObjectBase)</c> in place of
/// <c>typeof(UnityEngine.ScriptableObject)</c> when calling
/// <c>IsOwnedElementTypeCore</c>.
/// </summary>
public class TestScriptableObjectBase
{
}

/// <summary>
/// Stub for the test-side DataTemplate base. Same role as
/// <see cref="TestScriptableObjectBase"/>: tests pass it in instead of
/// the real Il2CppMenace.Tools.DataTemplate.
/// </summary>
public class TestDataTemplateBase : TestScriptableObjectBase
{
}

/// <summary>
/// Mirrors <c>SkillEventHandlerTemplate</c>: an abstract polymorphic
/// ScriptableObject-shaped base with concrete subclasses. The shape
/// that drives clone-time deep-copy decisions for owned-element
/// collections.
/// </summary>
public abstract class TestPolymorphicHandlerBase : TestScriptableObjectBase
{
}

public class TestConcreteHandlerA : TestPolymorphicHandlerBase
{
}

public class TestConcreteHandlerB : TestPolymorphicHandlerBase
{
}

/// <summary>
/// Mirrors <c>SkillGroup</c> / <c>DefectGroup</c>: a concrete
/// ScriptableObject-shaped wrapper with no subclasses. Conceptually a
/// shared registry resource; the deep-copy rule must NOT pick this up.
/// </summary>
public class TestConcreteWrapper : TestScriptableObjectBase
{
}

/// <summary>
/// Mirrors a DataTemplate descendant: registry-identified by
/// <c>m_ID</c>, intentional sharing, must never be deep-copied on
/// parent clone.
/// </summary>
public class TestDataTemplateLike : TestDataTemplateBase
{
}
