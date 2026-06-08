using System;
using Jiangyu.Game.Audio;
using Jiangyu.Game.Ui;
using UnityEngine.UIElements;

namespace Jiangyu.Game.Ui.Components;

/// <summary>
/// A native-looking text button: the game's <c>.text-button</c> frame with a
/// <c>.text-button-label</c>, the game's UI click sound on press, and the game's native
/// hover glow. It is an open wrapper, not a sealed widget: <see cref="Root"/> is the real
/// <c>UnityEngine.UIElements.Button</c>, so anything not exposed here is reachable on it
/// (inline styles, extra USS classes, child elements).
/// </summary>
public sealed class TextButton
{
    /// <summary>The underlying button element. Add it to the tree, restyle it, extend it.</summary>
    public UnityEngine.UIElements.Button Root { get; }

    /// <summary>Build the button. Pass <paramref name="sound"/> false to suppress the click sound.</summary>
    public TextButton(string text, bool sound = true)
    {
        Root = new UnityEngine.UIElements.Button();
        Root.AddToClassList("text-button");
        var label = new Label(text);
        label.AddToClassList("text-button-label");
        Root.Add(label);
        if (sound)
            Root.clickable.clicked += (Action)Sound.Click;
        Root.WireNativeHover();
    }

    /// <summary>Run <paramref name="handler"/> on click (in addition to the click sound).</summary>
    public TextButton OnClick(Action handler)
    {
        if (handler != null)
            Root.clickable.clicked += (Action)handler;
        return this;
    }
}
