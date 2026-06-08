using UnityEngine.UIElements;

namespace Jiangyu.Game.Ui.Components;

/// <summary>
/// A window-framed panel that can dismiss itself on any outside click. Wraps the game's
/// <c>.window</c> / <c>.unit-window-background</c> / <c>.unit-window-border</c> frame. Add
/// rows to <see cref="Content"/>, inject <see cref="Root"/> where you want the panel, and
/// show or hide it with <see cref="Show"/> / <see cref="Hide"/>. It is an open wrapper:
/// <see cref="Root"/> and <see cref="Content"/> are real elements you can restyle or
/// extend.
/// </summary>
public sealed class Flyout
{
    /// <summary>The window frame element. Inject this.</summary>
    public VisualElement Root { get; }

    /// <summary>The content area inside the frame. Add your rows here.</summary>
    public VisualElement Content { get; }

    public Flyout()
    {
        Root = new VisualElement();
        Root.AddToClassList("window");

        var background = new VisualElement();
        background.AddToClassList("unit-window-background");
        Root.Add(background);

        var border = new VisualElement();
        border.AddToClassList("unit-window-border");
        Root.Add(border);

        Content = new VisualElement();
        Root.Add(Content);
    }

    /// <summary>
    /// Dismiss the flyout when a pointer goes down outside it. Names in
    /// <paramref name="keepOpenOn"/> (the toggle button, say) are left alone. Call this
    /// once <see cref="Root"/> is attached to a panel.
    /// </summary>
    public Flyout DismissOnOutsideClick(params string[] keepOpenOn)
    {
        UI.CloseOnOutsideClick(Root, null, keepOpenOn);
        return this;
    }

    /// <summary>Show the flyout.</summary>
    public void Show() => Root.SetVisible(true);

    /// <summary>Hide the flyout.</summary>
    public void Hide() => Root.SetVisible(false);
}
