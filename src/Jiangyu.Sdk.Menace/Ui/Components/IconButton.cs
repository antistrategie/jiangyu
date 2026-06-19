using System;
using Il2CppInterop.Runtime;
using Jiangyu.Game.Audio;
using UnityEngine;
using UnityEngine.UIElements;

namespace Jiangyu.Game.Ui.Components;

/// <summary>
/// A native-looking icon button: a UI Toolkit <c>Button</c> wearing the game's icon-button USS
/// classes (<c>button</c> + <c>image-tint-interact</c>, which give the native icon tint and the
/// hover-tint), the glyph set as its background image, and the game's UI click sound. It is an open
/// wrapper, not a sealed widget: <see cref="Root"/> is the real <c>UnityEngine.UIElements.Button</c>,
/// so anything not exposed here is reachable on it. Sibling of <see cref="TextButton"/>. Use this
/// when an icon should stand in for a text label to save header space.
/// </summary>
public sealed class IconButton
{
    /// <summary>The underlying button element. Add it to the tree, restyle it, extend it.</summary>
    public UnityEngine.UIElements.Button Root { get; }

    // Held so the converted hover delegates have an explicit managed owner for the element's lifetime,
    // alongside the element's own callback registry that keeps them alive.
    private readonly EventCallback<PointerEnterEvent> _onPointerEnter;
    private readonly EventCallback<PointerLeaveEvent> _onPointerLeave;

    /// <summary>Build the button (24x24 by default). Pass <paramref name="sound"/> false to suppress the click sound.</summary>
    public IconButton(bool sound = true)
    {
        Root = new UnityEngine.UIElements.Button();
        Root.AddToClassList("button");
        Root.AddToClassList("image-tint-interact");
        Root.style.width = new StyleLength(24f);
        Root.style.height = new StyleLength(24f);
        if (sound)
            Root.clickable.clicked += (Action)Sound.Click;

        // Hover: brighten the glyph tint, matching the game's native icon buttons.
        var rest = new StyleColor(new Color(188f / 255f, 176f / 255f, 150f / 255f));
        var hover = new StyleColor(new Color(238f / 255f, 227f / 255f, 190f / 255f));
        Root.style.unityBackgroundImageTintColor = rest;
        _onPointerEnter = DelegateSupport.ConvertDelegate<EventCallback<PointerEnterEvent>>(
            (Action<PointerEnterEvent>)(_ => Root.style.unityBackgroundImageTintColor = hover));
        _onPointerLeave = DelegateSupport.ConvertDelegate<EventCallback<PointerLeaveEvent>>(
            (Action<PointerLeaveEvent>)(_ => Root.style.unityBackgroundImageTintColor = rest));
        Root.RegisterCallback<PointerEnterEvent>(_onPointerEnter);
        Root.RegisterCallback<PointerLeaveEvent>(_onPointerLeave);
    }

    /// <summary>Set the glyph from a sprite.</summary>
    public IconButton SetIcon(Sprite sprite)
    {
        if (sprite != null)
            Root.style.backgroundImage = new StyleBackground(sprite);
        return this;
    }

    /// <summary>Set the glyph from a texture (e.g. a bundled PNG loaded via <c>Context.Assets.Load</c>).</summary>
    public IconButton SetIcon(Texture2D texture)
    {
        if (texture != null)
            Root.style.backgroundImage = new StyleBackground(texture);
        return this;
    }

    /// <summary>Resize the button (square icons fill a square button).</summary>
    public IconButton SetSize(float width, float height)
    {
        Root.style.width = new StyleLength(width);
        Root.style.height = new StyleLength(height);
        return this;
    }

    /// <summary>Override the glyph tint. The <c>image-tint-interact</c> class supplies the native
    /// tint and hover otherwise, so this is only needed to force a specific colour.</summary>
    public IconButton SetTint(Color color)
    {
        Root.style.unityBackgroundImageTintColor = new StyleColor(color);
        return this;
    }

    /// <summary>Run <paramref name="handler"/> on click (in addition to the click sound).</summary>
    public IconButton OnClick(Action handler)
    {
        if (handler != null)
            Root.clickable.clicked += (Action)handler;
        return this;
    }
}
