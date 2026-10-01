using System;
using Il2CppInterop.Runtime;
using Jiangyu.Game.Audio;
using Jiangyu.Game.Ui;
using UnityEngine.UIElements;

namespace Jiangyu.Game.Ui.Components;

/// <summary>
/// A native-looking text button, built the way the game builds its own: a
/// <c>.text-button</c> root (the standard button frame) holding a <c>.text-button-label</c>
/// and a <c>Hover</c> child with the <c>.text-button-hover</c> class, an absolute fill that
/// the game's stylesheet paints with the standard hover art. The hover child shows while the
/// pointer is over an enabled button, as the game's own text buttons do, and the game's UI
/// click sound plays on press. It is an open wrapper, not a sealed widget: <see cref="Root"/>
/// is the real <c>UnityEngine.UIElements.Button</c>, so anything not exposed here is reachable
/// on it (inline styles, extra USS classes, child elements).
/// </summary>
public sealed class TextButton
{
    /// <summary>The underlying button element. Add it to the tree, restyle it, extend it.</summary>
    public UnityEngine.UIElements.Button Root { get; }

    /// <summary>The hover overlay (<c>.text-button-hover</c>), shown while the pointer is over the button.</summary>
    public VisualElement Hover { get; }

    /// <summary>The button's label (<c>.text-button-label</c>).</summary>
    public Label Label { get; }

    // Held so the converted hover delegates have an explicit managed owner for the element's lifetime,
    // alongside the element's own callback registry that keeps them alive.
    private readonly EventCallback<PointerEnterEvent> _onPointerEnter;
    private readonly EventCallback<PointerLeaveEvent> _onPointerLeave;

    /// <summary>Build the button. Pass <paramref name="sound"/> false to suppress the click sound.</summary>
    public TextButton(string text, bool sound = true)
    {
        Root = new UnityEngine.UIElements.Button();
        Root.AddToClassList("text-button");

        Hover = UiElementExtensions.FillOverlay();
        Hover.name = "Hover";
        Hover.AddToClassList("text-button-hover");
        Hover.SetVisible(false);
        Root.Add(Hover);

        Label = new Label(text);
        Label.AddToClassList("text-button-label");
        Root.Add(Label);

        if (sound)
            Root.clickable.clicked += (Action)Sound.Click;

        // A disabled button keeps its resting look, so the overlay only shows on an enabled one.
        _onPointerEnter = DelegateSupport.ConvertDelegate<EventCallback<PointerEnterEvent>>(
            (Action<PointerEnterEvent>)(_ => Hover.SetVisible(Root.enabledInHierarchy)));
        _onPointerLeave = DelegateSupport.ConvertDelegate<EventCallback<PointerLeaveEvent>>(
            (Action<PointerLeaveEvent>)(_ => Hover.SetVisible(false)));
        Root.RegisterCallback<PointerEnterEvent>(_onPointerEnter);
        Root.RegisterCallback<PointerLeaveEvent>(_onPointerLeave);
    }

    /// <summary>
    /// Apply one of the game's button styles on top of the standard frame, the way the game's own
    /// <c>TextButton</c> applies its <c>button-style</c> and <c>hover-style</c>: the style class goes
    /// on the button and its <c>-hover</c> class on the hover overlay. The game ships
    /// <c>old-text-button</c> (the unit window's header buttons), <c>positive-text-button</c>,
    /// <c>negative-text-button</c> and <c>neutral-text-button</c>.
    /// </summary>
    public TextButton WithStyle(string style)
    {
        if (string.IsNullOrEmpty(style))
            return this;
        Root.AddToClassList(style);
        Hover.AddToClassList(style + "-hover");
        return this;
    }

    /// <summary>Run <paramref name="handler"/> on click (in addition to the click sound).</summary>
    public TextButton OnClick(Action handler)
    {
        if (handler != null)
            Root.clickable.clicked += (Action)handler;
        return this;
    }
}
