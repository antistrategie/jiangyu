using System;
using UnityEngine.UIElements;

namespace Jiangyu.Game;

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
}
