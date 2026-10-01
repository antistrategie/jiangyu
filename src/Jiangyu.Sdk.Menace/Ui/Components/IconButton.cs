using System;
using Il2CppInterop.Runtime;
using Jiangyu.Game.Audio;
using UnityEngine;
using UnityEngine.UIElements;

namespace Jiangyu.Game.Ui.Components;

/// <summary>
/// A native-looking icon button, built like the game's own <c>IconButton</c> (the unit window's
/// statistics button, for example): a 24x24 UI Toolkit <c>Button</c> wearing the <c>button</c> class,
/// holding a <c>Background</c> child with the game's <c>icon-button-background-1</c> frame and an
/// <c>Icon</c> child carrying the glyph with the <c>image-tint-interact</c> tint, plus the game's UI
/// click sound. It is an open wrapper, not a sealed widget: <see cref="Root"/>, <see cref="Background"/>
/// and <see cref="Icon"/> are real elements to restyle or extend. Sibling of <see cref="TextButton"/>.
/// Use this when an icon should stand in for a text label to save header space.
/// </summary>
public sealed class IconButton
{
    /// <summary>The underlying button element. Add it to the tree, restyle it, extend it.</summary>
    public UnityEngine.UIElements.Button Root { get; }

    /// <summary>The framed backing behind the glyph.</summary>
    public VisualElement Background { get; }

    /// <summary>The glyph element.</summary>
    public VisualElement Icon { get; }

    // Held so the converted hover delegates have an explicit managed owner for the element's lifetime,
    // alongside the element's own callback registry that keeps them alive.
    private readonly EventCallback<PointerEnterEvent> _onPointerEnter;
    private readonly EventCallback<PointerLeaveEvent> _onPointerLeave;

    /// <summary>Build the button (24x24 by default). Pass <paramref name="sound"/> false to suppress the click sound.</summary>
    public IconButton(bool sound = true)
    {
        Root = new UnityEngine.UIElements.Button();
        Root.AddToClassList("button");
        Root.style.width = new StyleLength(24f);
        Root.style.height = new StyleLength(24f);
        Root.style.paddingLeft = Root.style.paddingRight = Root.style.paddingTop = Root.style.paddingBottom = 0;

        Background = FillChild("Background");
        Background.AddToClassList("icon-button-background-1");
        Root.Add(Background);

        Icon = FillChild("Icon");
        Icon.AddToClassList("image-tint-interact");
        Root.Add(Icon);

        if (sound)
            Root.clickable.clicked += (Action)Sound.Click;

        // Hover: brighten the glyph tint, matching the game's native icon buttons.
        var rest = new StyleColor(new Color(188f / 255f, 176f / 255f, 150f / 255f));
        var hover = new StyleColor(new Color(238f / 255f, 227f / 255f, 190f / 255f));
        Icon.style.unityBackgroundImageTintColor = rest;
        _onPointerEnter = DelegateSupport.ConvertDelegate<EventCallback<PointerEnterEvent>>(
            (Action<PointerEnterEvent>)(_ => Icon.style.unityBackgroundImageTintColor = hover));
        _onPointerLeave = DelegateSupport.ConvertDelegate<EventCallback<PointerLeaveEvent>>(
            (Action<PointerLeaveEvent>)(_ => Icon.style.unityBackgroundImageTintColor = rest));
        Root.RegisterCallback<PointerEnterEvent>(_onPointerEnter);
        Root.RegisterCallback<PointerLeaveEvent>(_onPointerLeave);
    }

    private static VisualElement FillChild(string name)
    {
        var child = new VisualElement { name = name, pickingMode = PickingMode.Ignore };
        child.style.position = Position.Absolute;
        child.style.left = child.style.top = child.style.right = child.style.bottom = 0;
        return child;
    }

    /// <summary>Set the glyph from a sprite.</summary>
    public IconButton SetIcon(Sprite sprite)
    {
        if (sprite != null)
            Icon.style.backgroundImage = new StyleBackground(sprite);
        return this;
    }

    /// <summary>Set the glyph from a texture (e.g. a bundled PNG loaded via <c>Context.Assets.Load</c>).</summary>
    public IconButton SetIcon(Texture2D texture)
    {
        if (texture != null)
            Icon.style.backgroundImage = new StyleBackground(texture);
        return this;
    }

    /// <summary>Resize the button. The frame and glyph fill it.</summary>
    public IconButton SetSize(float width, float height)
    {
        Root.style.width = new StyleLength(width);
        Root.style.height = new StyleLength(height);
        return this;
    }

    /// <summary>Override the glyph tint. The button supplies the native tint and hover otherwise,
    /// so this is only needed to force a specific colour.</summary>
    public IconButton SetTint(Color color)
    {
        Icon.style.unityBackgroundImageTintColor = new StyleColor(color);
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
