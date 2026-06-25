using System;
using Il2CppInterop.Runtime;
using Il2CppMenace.UI;
using Jiangyu.Sdk;
using UnityEngine.UIElements;

namespace Jiangyu.Game.Ui.Components;

/// <summary>
/// The game's native tooltip: the floating info panel the game shows on hover, built from the
/// game's <c>TooltipData</c> and displayed through <c>UIManager</c>. Build content with the fluent
/// <see cref="Heading"/> / <see cref="Paragraph"/> / <see cref="Line"/> / <see cref="Space"/>
/// methods, then <see cref="Show"/> it anchored to an element (it sticks to the mouse by default,
/// matching the game's own tooltips), or wire it to an element's hover with the static
/// <see cref="OnHover"/>. <see cref="Hide"/> dismisses whatever tooltip is up.
///
/// <para>It is an ergonomics wrapper, not a sealed widget: a mod-facing <see cref="Style"/> maps
/// onto the game's paragraph styles so the Il2Cpp tooltip types stay off the mod's surface, and
/// <see cref="Data"/> exposes the underlying <c>TooltipData</c> for content this wrapper does not
/// surface. Pass already-localised text (route it through <c>Locale.Text</c> first): each row is
/// registered into the tooltip's own loca store so it renders, and the store falls back to the
/// string as given, so the authored text is what shows.</para>
/// </summary>
public sealed class Tooltip
{
    /// <summary>Paragraph emphasis, mapped onto the game's native tooltip paragraph styles.</summary>
    public enum Style
    {
        Default,
        Description,
        Hint,
        Positive,
        Negative,
        Warning,
        Disabled,
    }

    // The native Add* methods marshal their Il2CppSystem.Nullable<int> _iconSize through
    // Il2CppObjectBaseToPtrNotNull, so a null (the proxy's own default included) throws. A boxed
    // empty Nullable is the "no icon size" value the native side expects.
    private static readonly Il2CppSystem.Nullable<int> NoIconSize = new();

    private readonly TooltipData _data;

    /// <summary>Start an empty tooltip. <paramref name="width"/> is the content width in pixels.</summary>
    public Tooltip(string name = "jiangyu-tooltip", int width = 250)
    {
        _data = new TooltipData(name, width);
    }

    /// <summary>The underlying game tooltip data, for content this wrapper does not surface.</summary>
    public TooltipData Data => _data;

    /// <summary>Add a heading row.</summary>
    public Tooltip Heading(string text)
    {
        // _translate: true registers the text into the tooltip's loca store so it renders. The store
        // falls back to the given string, so already-localised text shows as authored.
        try { _data.AddHeading(text, null, NoIconSize, true); }
        catch (Exception ex) { Log.Warn($"Tooltip.Heading failed: {ex.Message}"); }
        return this;
    }

    /// <summary>Add a subheading row (a smaller heading).</summary>
    public Tooltip Subheading(string text)
    {
        try { _data.AddSubheading(text, null, NoIconSize, true); }
        catch (Exception ex) { Log.Warn($"Tooltip.Subheading failed: {ex.Message}"); }
        return this;
    }

    /// <summary>Add a paragraph row in the given <paramref name="style"/>.</summary>
    public Tooltip Paragraph(string text, Style style = Style.Default)
    {
        try { _data.AddParagraph(text, Map(style), null, NoIconSize, true, false); }
        catch (Exception ex) { Log.Warn($"Tooltip.Paragraph failed: {ex.Message}"); }
        return this;
    }

    /// <summary>Add a horizontal divider.</summary>
    public Tooltip Line()
    {
        try { _data.AddHorizontalLine(); }
        catch (Exception ex) { Log.Warn($"Tooltip.Line failed: {ex.Message}"); }
        return this;
    }

    /// <summary>Add vertical spacing.</summary>
    public Tooltip Space()
    {
        try { _data.AddSpace(); }
        catch (Exception ex) { Log.Warn($"Tooltip.Space failed: {ex.Message}"); }
        return this;
    }

    /// <summary>
    /// Show this tooltip, anchored to <paramref name="anchor"/>. With <paramref name="stickToMouse"/>
    /// (the default) it follows the cursor like the game's own tooltips, otherwise it pins to the
    /// anchor. Call once the anchor is attached to a panel.
    /// </summary>
    public void Show(VisualElement anchor, bool stickToMouse = true)
    {
        try { UIManager.Get()?.ShowTooltip(_data, anchor, false, stickToMouse); }
        catch { }
    }

    /// <summary>Dismiss whatever tooltip is currently shown.</summary>
    public static void Hide()
    {
        try { UIManager.Get()?.HideTooltip(); } catch { }
    }

    // The anchor whose OnHover tooltip is currently showing. Tracked so a leave on one anchor does
    // not tear down a tooltip a different anchor just opened (adjacent anchors: B's enter fires
    // before A's leave, and a blind Hide() on A's leave would dismiss B's tooltip).
    private static VisualElement _hoverAnchor;

    /// <summary>
    /// Show a freshly-built tooltip while <paramref name="anchor"/> is hovered, and hide it on leave.
    /// <paramref name="build"/> runs on every hover, so the tooltip reflects current state. Return
    /// null from it to show nothing this time. Call once the anchor is attached to a panel.
    /// </summary>
    public static void OnHover(VisualElement anchor, Func<Tooltip> build, bool stickToMouse = true)
    {
        if (anchor == null || build == null)
            return;
        try
        {
            anchor.RegisterCallback<PointerEnterEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerEnterEvent>>(
                (Action<PointerEnterEvent>)(_ =>
                {
                    Tooltip tooltip = null;
                    try { tooltip = build(); } catch { }
                    if (tooltip == null)
                        return;
                    _hoverAnchor = anchor;
                    tooltip.Show(anchor, stickToMouse);
                })));
            anchor.RegisterCallback<PointerLeaveEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerLeaveEvent>>(
                (Action<PointerLeaveEvent>)(_ =>
                {
                    // Only dismiss if this anchor's tooltip is the one showing: leaving A after
                    // entering the adjacent B must not close B's tooltip.
                    if (!ReferenceEquals(_hoverAnchor, anchor))
                        return;
                    _hoverAnchor = null;
                    Hide();
                })));
        }
        catch { }
    }

    private static ParagraphStyle Map(Style style) => style switch
    {
        Style.Description => ParagraphStyle.Description,
        Style.Hint => ParagraphStyle.Hint,
        Style.Positive => ParagraphStyle.Positive,
        Style.Negative => ParagraphStyle.Negative,
        Style.Warning => ParagraphStyle.Warning,
        Style.Disabled => ParagraphStyle.Disabled,
        _ => ParagraphStyle.Default,
    };
}
