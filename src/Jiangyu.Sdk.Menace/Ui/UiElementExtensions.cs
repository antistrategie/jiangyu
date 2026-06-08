using System;
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

    /// <summary>
    /// Position <paramref name="target"/> absolutely one row below <paramref name="reference"/>,
    /// matching its left edge, width, and height, with an optional <paramref name="gap"/>.
    /// This is the way to add a sibling to a game element that has no USS classes and is
    /// absolutely positioned (so <see cref="MatchStyle"/> has nothing to copy), for
    /// example stacking a third bar under the health and armour bars. Read from the
    /// reference's resolved layout, so call it after the reference has been laid out.
    /// </summary>
    public static void StackAfter(this VisualElement target, VisualElement reference, float gap = 0f)
    {
        if (target == null || reference == null)
            return;

        float top, left, width, height;
        try
        {
            var box = reference.resolvedStyle;
            top = box.top;
            left = box.left;
            width = box.width;
            height = box.height;
        }
        catch { return; }

        try
        {
            target.style.position = new StyleEnum<Position>(Position.Absolute);
            target.style.left = new StyleLength(left);
            target.style.width = new StyleLength(width);
            target.style.height = new StyleLength(height);
            target.style.top = new StyleLength(top + height + gap);
        }
        catch (Exception)
        {
            // Layout not ready, or a style setter rejected the value; leave as-is.
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

    // The game's hover glow, captured off the first native "Hover" donor found in a live
    // panel that carries a background image, then reused for every control. Readiness is
    // just whether the cached image is still alive (see GlowAlive) — no separate flag.
    private static Background _glow;
    private static Color _glowTint;

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

    // Give an interactive control the game's native hover glow. A fill overlay, painted
    // from a native donor discovered in the live panel on first hover and reused from then
    // on, fades in only while the pointer is over the control; it ignores picking so it
    // never intercepts the control's own clicks. Internal because hover is a trait the
    // Components wire for themselves, not a step a mod performs.
    internal static void WireNativeHover(this VisualElement target)
    {
        if (target == null)
            return;

        VisualElement overlay;
        try
        {
            overlay = FillOverlay();
            overlay.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.None);
            target.Add(overlay);
        }
        catch { return; }

        target.RegisterCallback<PointerEnterEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerEnterEvent>>(
            (Action<PointerEnterEvent>)(_ =>
            {
                if (PaintGlow(overlay, target))
                    overlay.SetVisible(true);
            })));
        target.RegisterCallback<PointerLeaveEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerLeaveEvent>>(
            (Action<PointerLeaveEvent>)(_ => overlay.SetVisible(false))));
    }

    // Paint the overlay with the captured glow, (re)capturing it first if needed. The
    // cache is dropped when its donor sprite/texture has gone away (a scene reload can
    // destroy the element it was copied from), so a stale image is re-discovered from the
    // live panel rather than painted blank. Returns false until a donor is found.
    private static bool PaintGlow(VisualElement overlay, VisualElement anchor)
    {
        if (!GlowAlive())
            CaptureGlow(anchor);
        if (!GlowAlive())
            return false;
        try
        {
            overlay.style.backgroundImage = new StyleBackground(_glow);
            overlay.style.unityBackgroundImageTintColor = new StyleColor(_glowTint);
            return true;
        }
        catch { return false; }
    }

    // Whether the cached glow still references a live image. Unity overrides == for
    // destroyed objects, so a donor torn down on a scene change reads back as null here.
    private static bool GlowAlive()
    {
        try { return _glow.sprite != null || _glow.texture != null || _glow.vectorImage != null; }
        catch { return false; }
    }

    // Find the glow once: the first element named "Hover" in the panel that actually
    // carries a background image. The game pins such a child on its native buttons and slots.
    private static void CaptureGlow(VisualElement anchor)
    {
        try
        {
            var root = anchor?.panel?.visualTree;
            if (root == null)
                return;
            var donors = UI.FindAll(root, UiSelector.Name("Hover"));
            for (var i = 0; i < donors.Count; i++)
            {
                try
                {
                    var bg = donors[i].resolvedStyle.backgroundImage;
                    if (bg.sprite == null && bg.texture == null && bg.vectorImage == null)
                        continue;
                    _glow = bg;
                    _glowTint = donors[i].resolvedStyle.unityBackgroundImageTintColor;
                    return;
                }
                catch { }
            }
        }
        catch { }
    }
}
