using System;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine.UIElements;

namespace Jiangyu.Game.Ui;

/// <summary>
/// Matches a live <see cref="VisualElement"/> by name, USS class, or concrete type.
/// Selectors locate anchors and scopes for <see cref="UI"/> injection. Find the
/// names and classes to match with the Studio UI inspector. Criteria combine with
/// <see cref="And"/>, and all must hold for a match.
/// </summary>
public sealed class UiSelector
{
    private readonly Func<VisualElement, bool> _match;

    private UiSelector(Func<VisualElement, bool> match) => _match = match;

    /// <summary>Match the element whose <see cref="VisualElement.name"/> equals <paramref name="name"/>.</summary>
    public static UiSelector Name(string name) =>
        new(e => NameOf(e) == name);

    /// <summary>Match an element carrying the USS class <paramref name="className"/>.</summary>
    public static UiSelector Class(string className) =>
        new(e => UiTree.HasClass(e, className));

    /// <summary>Match an element whose concrete game type is <typeparamref name="T"/> (a subclass counts).</summary>
    public static UiSelector Type<T>() where T : VisualElement =>
        new(e => Is<T>(e));

    /// <summary>
    /// Match an element whose concrete game type name equals <paramref name="typeName"/>,
    /// for example <c>"ArmoryUnitSelectSlot"</c>. This is the type name the UI inspector
    /// reports, so it matches even when the type has no compile-time wrapper to use with
    /// <see cref="Type{T}"/>.
    /// </summary>
    public static UiSelector TypeName(string typeName) =>
        new(e => UiTree.ConcreteTypeName(e) == typeName);

    /// <summary>A selector that matches only when both this and <paramref name="other"/> match.</summary>
    public UiSelector And(UiSelector other) =>
        new(e => _match(e) && other._match(e));

    /// <summary>Match an element whose name begins with <paramref name="prefix"/>.</summary>
    internal static UiSelector NameStartsWith(string prefix) =>
        new(e => NameOf(e) is { } name && name.StartsWith(prefix, StringComparison.Ordinal));

    internal bool Matches(VisualElement element) => element != null && _match(element);

    private static string NameOf(VisualElement e)
    {
        try { return e.name; }
        catch { return null; }
    }

    private static bool Is<T>(Il2CppObjectBase e) where T : VisualElement
    {
        try { return e.TryCast<T>() != null; }
        catch { return false; }
    }
}
