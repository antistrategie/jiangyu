using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UIElements;

namespace Jiangyu.Game.Ui;

/// <summary>Helpers for styling and placing injected UI to match the game's own elements.</summary>
public static class UiElementExtensions
{
    /// <summary>
    /// Copy every USS class off <paramref name="reference"/> onto <paramref name="target"/>,
    /// so the target picks up the same game stylesheet rules and lays out like it. Use
    /// it to make an injected element match a neighbour, for example a relationship bar
    /// matching the health bar beside it. Note: many game elements carry no USS classes
    /// and are positioned by name/id selector or in code, in which case there is nothing
    /// to copy and <see cref="StackAfter"/> is the tool to reach for instead.
    /// </summary>
    public static void MatchStyle(this VisualElement target, VisualElement reference)
    {
        if (target == null || reference == null)
            return;
        foreach (var className in UiTree.ClassesOf(reference))
        {
            try { target.AddToClassList(className); } catch { }
        }
    }

    // Targets StackAfter keeps positioned, keyed by native pointer, so a repeat call for the
    // same live target replaces its tracking rather than stacking a second geometry handler.
    // An entry is dropped when its target leaves the panel, so a recycled pointer starts clean.
    private static readonly Dictionary<IntPtr, Action> StackTracking = new();

    /// <summary>
    /// Position <paramref name="target"/> absolutely one row below <paramref name="reference"/>,
    /// matching its left edge, width, and height, with an optional <paramref name="gap"/>.
    /// This is the way to add a sibling to a game element that has no USS classes and is
    /// absolutely positioned (so <see cref="MatchStyle"/> has nothing to copy), for
    /// example stacking a third bar under the health and armour bars. The target follows
    /// the reference's layout: it is placed as soon as the reference has been laid out
    /// and moves whenever the reference's geometry changes, so it can be called from a
    /// bind callback before the screen's first layout pass. The target and reference must
    /// share a parent.
    /// </summary>
    public static void StackAfter(this VisualElement target, VisualElement reference, float gap = 0f)
    {
        if (target == null || reference == null)
            return;

        IntPtr targetPtr;
        try { targetPtr = target.Pointer; }
        catch { return; }

        if (StackTracking.TryGetValue(targetPtr, out var untrack))
            untrack();

        EventCallback<GeometryChangedEvent> onGeometry = null;
        EventCallback<DetachFromPanelEvent> onDetach = null;
        void Untrack()
        {
            StackTracking.Remove(targetPtr);
            try { reference.UnregisterCallback(onGeometry, TrickleDown.NoTrickleDown); } catch { }
            try { target.UnregisterCallback(onDetach, TrickleDown.NoTrickleDown); } catch { }
        }

        onGeometry = DelegateSupport.ConvertDelegate<EventCallback<GeometryChangedEvent>>(
            (Action<GeometryChangedEvent>)(_ => PlaceBelow(target, reference, gap)));
        onDetach = DelegateSupport.ConvertDelegate<EventCallback<DetachFromPanelEvent>>(
            (Action<DetachFromPanelEvent>)(_ => Untrack()));

        try
        {
            reference.RegisterCallback(onGeometry, TrickleDown.NoTrickleDown);
            target.RegisterCallback(onDetach, TrickleDown.NoTrickleDown);
            StackTracking[targetPtr] = Untrack;
        }
        catch (Exception) { Untrack(); }

        PlaceBelow(target, reference, gap);
    }

    private static void PlaceBelow(VisualElement target, VisualElement reference, float gap)
    {
        Rect box;
        try { box = reference.layout; }
        catch { return; }

        // A reference that has not been through a layout pass reports NaN or an empty box.
        // Its GeometryChangedEvent places the target once it has.
        if (float.IsNaN(box.x) || float.IsNaN(box.y) || float.IsNaN(box.width) || float.IsNaN(box.height)
            || box.width <= 0f || box.height <= 0f)
            return;

        try
        {
            target.style.position = new StyleEnum<Position>(Position.Absolute);
            target.style.left = new StyleLength(box.x);
            target.style.width = new StyleLength(box.width);
            target.style.height = new StyleLength(box.height);
            target.style.top = new StyleLength(box.y + box.height + gap);
        }
        catch (Exception)
        {
            // A style setter rejected the value, so it is left as is.
        }
    }

    /// <summary>Show or hide <paramref name="target"/> by toggling its display style.</summary>
    public static void SetVisible(this VisualElement target, bool visible)
    {
        if (target == null)
            return;
        try { target.style.display = new StyleEnum<DisplayStyle>(visible ? DisplayStyle.Flex : DisplayStyle.None); }
        catch { }
    }

    /// <summary>Whether <paramref name="target"/> currently resolves to displayed.</summary>
    public static bool IsVisible(this VisualElement target)
    {
        try { return target != null && target.resolvedStyle.display == DisplayStyle.Flex; }
        catch { return false; }
    }

    /// <summary>Set <paramref name="target"/>'s width as a percentage of its parent (0 to 100), for bar fills.</summary>
    public static void SetWidthPercent(this VisualElement target, float percent)
    {
        if (target == null)
            return;
        try { target.style.width = new StyleLength(Length.Percent(percent)); }
        catch { }
    }

    /// <summary>
    /// Centre <paramref name="target"/>'s text both ways, inline. The game's own
    /// <c>.unity-label</c> rule sets a text-align that ties on specificity with a mod's USS
    /// class and wins on stylesheet load order, so an inline value is the reliable way to
    /// centre an injected label.
    /// </summary>
    public static void CenterText(this VisualElement target)
    {
        if (target == null)
            return;
        try { target.style.unityTextAlign = new StyleEnum<TextAnchor>(TextAnchor.MiddleCenter); }
        catch { }
    }

    // A transparent, picking-ignored element stretched to fill its parent: the shared
    // recipe behind hover overlays, selection borders and the outside-click catcher.
    internal static VisualElement FillOverlay()
    {
        var e = new VisualElement { pickingMode = PickingMode.Ignore };
        e.style.position = new StyleEnum<Position>(Position.Absolute);
        e.style.left = new StyleLength(0f);
        e.style.right = new StyleLength(0f);
        e.style.top = new StyleLength(0f);
        e.style.bottom = new StyleLength(0f);
        return e;
    }
}
