using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine.UIElements;

namespace Jiangyu.Game;

/// <summary>
/// Depth-bounded traversal of a live UI Toolkit tree. Every read is guarded so a
/// torn-down element returns a safe default rather than throwing mid-walk.
/// </summary>
internal static class UiTree
{
    private const int MaxDepth = 24;

    internal static int ChildCount(VisualElement e)
    {
        if (e == null)
            return 0;
        try { return e.childCount; }
        catch { return 0; }
    }

    // Make an element fill its parent (absolute, all edges pinned), so an injected
    // overlay covers the screen instead of collapsing to its wrapper's size.
    internal static void StretchFull(VisualElement e)
    {
        if (e == null)
            return;
        try
        {
            e.style.position = new StyleEnum<Position>(Position.Absolute);
            e.style.left = new StyleLength(0f);
            e.style.top = new StyleLength(0f);
            e.style.right = new StyleLength(0f);
            e.style.bottom = new StyleLength(0f);
        }
        catch { }
    }

    internal static List<string> ClassesOf(VisualElement e)
    {
        var result = new List<string>();
        if (e == null)
            return result;
        try
        {
            var classes = e.GetClassesForIteration();
            if (classes != null)
                for (var i = 0; i < classes.Count; i++)
                    result.Add(classes[i]);
        }
        catch { }
        return result;
    }

    // The concrete IL2CPP class name of a live element, for example
    // "ArmoryUnitSelectSlot", not the VisualElement cast type. This is the type name
    // the UI inspector reports, so a selector matches against it directly.
    internal static string ConcreteTypeName(VisualElement e)
    {
        if (e == null)
            return null;
        try
        {
            var ptr = e.Pointer;
            if (ptr == IntPtr.Zero)
                return null;
            var klass = IL2CPP.il2cpp_object_get_class(ptr);
            return Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
        }
        catch { return null; }
    }

    // Scans the live class list directly rather than via ClassesOf, since this runs
    // per child during re-apply (OccupiedBySelf) and per element during selector
    // matching, and only needs membership, not a copy.
    internal static bool HasClass(VisualElement e, string className)
    {
        if (e == null)
            return false;
        try
        {
            var classes = e.GetClassesForIteration();
            if (classes != null)
                for (var i = 0; i < classes.Count; i++)
                    if (classes[i] == className)
                        return true;
        }
        catch { }
        return false;
    }

    internal static VisualElement ChildAt(VisualElement e, int index)
    {
        try { return e.ElementAt(index); }
        catch { return null; }
    }

    internal static int IndexInParent(VisualElement child)
    {
        var parent = ParentOf(child);
        if (parent == null)
            return -1;
        try { return parent.IndexOf(child); }
        catch { return -1; }
    }

    internal static VisualElement ParentOf(VisualElement child)
    {
        if (child == null)
            return null;
        try { return child.parent; }
        catch { return null; }
    }

    internal static VisualElement FindFirst(VisualElement root, UiSelector selector)
    {
        if (root == null || selector == null)
            return null;
        return FindFirst(root, selector, 0);
    }

    internal static List<VisualElement> FindAll(VisualElement root, UiSelector selector)
    {
        var into = new List<VisualElement>();
        if (root != null && selector != null)
            Collect(root, selector, into, 0);
        return into;
    }

    private static VisualElement FindFirst(VisualElement element, UiSelector selector, int depth)
    {
        if (element == null || depth > MaxDepth)
            return null;
        if (selector.Matches(element))
            return element;
        var count = ChildCount(element);
        for (var i = 0; i < count; i++)
        {
            var hit = FindFirst(ChildAt(element, i), selector, depth + 1);
            if (hit != null)
                return hit;
        }
        return null;
    }

    private static void Collect(VisualElement element, UiSelector selector, List<VisualElement> into, int depth)
    {
        if (element == null || depth > MaxDepth)
            return;
        if (selector.Matches(element))
            into.Add(element);
        var count = ChildCount(element);
        for (var i = 0; i < count; i++)
            Collect(ChildAt(element, i), selector, into, depth + 1);
    }
}
