using System;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using Il2CppMenace.UI;
using Jiangyu.Sdk;
using UnityEngine.UIElements;

namespace Jiangyu.Game.Ui.Components;

/// <summary>
/// The game's native tooltip: the floating info panel the game shows on hover, built from the
/// game's <c>TooltipData</c> and displayed through <c>UIManager</c>. Build content with the fluent
/// <see cref="Heading"/> / <see cref="Paragraph"/> / <see cref="Stat"/> / <see cref="Line"/>
/// methods, then <see cref="Show"/> it anchored to an element (it sticks to the mouse by default,
/// matching the game's own tooltips), or wire it to an element's hover with the static
/// <see cref="OnHover"/>. <see cref="Hide"/> takes this tooltip back down.
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

    // The same boxed-empty-Nullable trick as NoIconSize, for the optional Color? tint/bar arguments
    // the icon, image, progress-bar and stat builders take: a null proxy default would throw in
    // marshalling, so pass an empty Il2CppSystem.Nullable<Color> to mean "use the game's default".
    private static readonly Il2CppSystem.Nullable<UnityEngine.Color> NoColor = new();

    private readonly TooltipData _data;

    // The tooltip's id (== the ctor name), used to recognise this tooltip on the UIManager stack so
    // Hide and the OnHover leave only remove this tooltip, never one stacked on top of it.
    private readonly string _id;

    // Consecutive Stat/StatBar calls accumulate into one native stat group, flushed into the tooltip
    // when a non-stat row follows or at Show time, so N stat calls render as one aligned column
    // rather than N separate single-row groups (and cost one AddStats marshal, not N).
    private TooltipStats _pendingStats;

    /// <summary>
    /// Start an empty tooltip. <paramref name="width"/> is the content width in pixels. Set
    /// <paramref name="canBePinned"/> so the player can pin it in place (the game shows a pin hint);
    /// a pinned tooltip stays put, which is what lets the cursor move onto an interactive row to open
    /// a <see cref="Interactive"/> nested child.
    /// </summary>
    public Tooltip(string name = "jiangyu-tooltip", int width = 250, bool canBePinned = false)
    {
        _id = name;
        _data = new TooltipData(name, name, width, null, canBePinned);
    }

    /// <summary>The underlying game tooltip data, for content this wrapper does not surface.</summary>
    public TooltipData Data
    {
        get { FlushStats(); return _data; }
    }

    /// <summary>Add a heading row.</summary>
    public Tooltip Heading(string text)
    {
        FlushStats();
        // _translate: true registers the text into the tooltip's loca store so it renders. The store
        // falls back to the given string, so already-localised text shows as authored.
        try { _data.AddHeading(text, null, NoIconSize, NoColor, true); }
        catch (Exception ex) { Log.Warn($"Tooltip.Heading failed: {ex.Message}"); }
        return this;
    }

    /// <summary>Add a subheading row (a smaller heading).</summary>
    public Tooltip Subheading(string text)
    {
        FlushStats();
        try { _data.AddSubheading(text, null, NoIconSize, NoColor, true); }
        catch (Exception ex) { Log.Warn($"Tooltip.Subheading failed: {ex.Message}"); }
        return this;
    }

    /// <summary>Add a paragraph row in the given <paramref name="style"/>.</summary>
    public Tooltip Paragraph(string text, Style style = Style.Default)
    {
        FlushStats();
        try { _data.AddParagraph(text, Map(style), null, NoIconSize, NoColor, true, false); }
        catch (Exception ex) { Log.Warn($"Tooltip.Paragraph failed: {ex.Message}"); }
        return this;
    }

    /// <summary>Add a horizontal divider.</summary>
    public Tooltip Line()
    {
        FlushStats();
        try { _data.AddHorizontalLine(); }
        catch (Exception ex) { Log.Warn($"Tooltip.Line failed: {ex.Message}"); }
        return this;
    }

    /// <summary>Add vertical spacing.</summary>
    public Tooltip Space()
    {
        FlushStats();
        try { _data.AddSpace(); }
        catch (Exception ex) { Log.Warn($"Tooltip.Space failed: {ex.Message}"); }
        return this;
    }

    /// <summary>Add a section heading (a divider-style header that groups the rows beneath it).</summary>
    public Tooltip SectionHeading(string text)
    {
        FlushStats();
        try { _data.AddSectionHeading(text, null, NoIconSize, NoColor, true); }
        catch (Exception ex) { Log.Warn($"Tooltip.SectionHeading failed: {ex.Message}"); }
        return this;
    }

    /// <summary>Add an icon row from <paramref name="sprite"/> (sourced by the mod, e.g. a template's icon).</summary>
    public Tooltip Icon(UnityEngine.Sprite sprite, string styleClass = null)
    {
        if (sprite == null) return this;
        FlushStats();
        try { _data.AddIcon(sprite, NoColor, styleClass); }
        catch (Exception ex) { Log.Warn($"Tooltip.Icon failed: {ex.Message}"); }
        return this;
    }

    /// <summary>
    /// Add an image row from <paramref name="sprite"/>. With <paramref name="useImageSize"/> the row
    /// takes the sprite's native size, otherwise it fits the tooltip width.
    /// </summary>
    public Tooltip Image(UnityEngine.Sprite sprite, bool useImageSize = true, string styleClass = null)
    {
        if (sprite == null) return this;
        FlushStats();
        try { _data.AddImage(sprite, NoColor, styleClass, useImageSize); }
        catch (Exception ex) { Log.Warn($"Tooltip.Image failed: {ex.Message}"); }
        return this;
    }

    /// <summary>
    /// Add a labelled progress bar. <paramref name="fraction"/> is the 0..1 fill; <paramref name="text"/>
    /// is the label drawn on it. Colours follow the game's default bar styling.
    /// </summary>
    public Tooltip ProgressBar(float fraction, string text = null, int sectionCount = 0)
    {
        FlushStats();
        try { _data.AddProgressBar(fraction, text, sectionCount, NoColor, NoColor, NoColor, NoColor, null, NoIconSize, true); }
        catch (Exception ex) { Log.Warn($"Tooltip.ProgressBar failed: {ex.Message}"); }
        return this;
    }

    /// <summary>
    /// Add a name/value stat row (e.g. "RANGE   5"). Pass <paramref name="nested"/> to reveal a child
    /// tooltip when the row is hovered, the way the game lets you drill into a stat from an item tooltip.
    /// Consecutive stat rows render as one aligned group.
    /// </summary>
    public Tooltip Stat(string name, string value, Func<Tooltip> nested = null)
    {
        try { (_pendingStats ??= new TooltipStats()).AddStat(name, value, NestedFunc(nested)); }
        catch (Exception ex) { Log.Warn($"Tooltip.Stat failed: {ex.Message}"); }
        return this;
    }

    /// <summary>
    /// Add a stat row drawn as a value-vs-maximum bar (e.g. a weapon's RANGE or DAMAGE). With
    /// <paramref name="biggerIsBetter"/> the bar colours a high value positively. Pass
    /// <paramref name="nested"/> to reveal a child tooltip when the row is hovered. Consecutive stat
    /// rows render as one aligned group.
    /// </summary>
    public Tooltip StatBar(string name, float value, float max, bool biggerIsBetter = true, Func<Tooltip> nested = null)
    {
        try { (_pendingStats ??= new TooltipStats()).AddStat(name, value, max, 0f, biggerIsBetter, NoColor, 0, 0, 0f, NestedFunc(nested)); }
        catch (Exception ex) { Log.Warn($"Tooltip.StatBar failed: {ex.Message}"); }
        return this;
    }

    /// <summary>
    /// Wrap the rows added by <paramref name="content"/> in an interactive container and reveal
    /// <paramref name="nested"/>'s tooltip as a child panel when that block is hovered. This is the
    /// mechanism the game uses to let you examine an item's skills from its tooltip. Call it as many
    /// times as you like for multiple independently-nested blocks; <paramref name="nested"/> runs on
    /// each hover so the child reflects current state, and may return null to show nothing.
    /// </summary>
    public Tooltip Interactive(Action<Tooltip> content, Func<Tooltip> nested)
    {
        if (content == null) return this;
        FlushStats();
        try
        {
            var container = _data.StartInteractiveContainer();
            try { content(this); }
            catch (Exception ex) { Log.Warn($"Tooltip.Interactive content failed: {ex.Message}"); }
            FlushStats();
            _data.EndInteractiveContainer();
            if (container != null && nested != null)
                container.SetCreateTooltipFunc(NestedFunc(nested));
        }
        catch (Exception ex) { Log.Warn($"Tooltip.Interactive failed: {ex.Message}"); }
        return this;
    }

    // Emit any accumulated stat rows as one native stat group at the current position. Called before
    // every non-stat row and at Show, so stats keep their authored order relative to other content.
    private void FlushStats()
    {
        if (_pendingStats == null) return;
        var stats = _pendingStats;
        _pendingStats = null;
        try { _data.AddStats(stats); }
        catch (Exception ex) { Log.Warn($"Tooltip stats flush failed: {ex.Message}"); }
    }

    // Wrap a mod-facing child-tooltip builder as the native Func<TooltipData> the interactive
    // elements and stat rows call on hover. Null builder -> null func (no child). Each invocation
    // is guarded so a throwing builder drops the child rather than faulting the hover.
    private static Il2CppSystem.Func<TooltipData> NestedFunc(Func<Tooltip> build)
    {
        if (build == null) return null;
        return DelegateSupport.ConvertDelegate<Il2CppSystem.Func<TooltipData>>(
            (Func<TooltipData>)(() =>
            {
                try { return build()?.Data; }
                catch (Exception ex) { Log.Warn($"Tooltip nested build failed: {ex.Message}"); return null; }
            }));
    }

    /// <summary>A rich-text span that renders <paramref name="text"/> as a link to the game's built-in
    /// glossary term <paramref name="key"/> (e.g. "hitpoint_damage"); hovering it opens that term's
    /// tooltip. Embed the result inside a <see cref="Paragraph"/>. Only existing game terms resolve.</summary>
    public static string Link(string key, string text) => $"<style=\"Link\"><link=\"{key}\">{text}</link></style>";

    /// <summary>
    /// Show this tooltip, anchored to <paramref name="anchor"/>. With <paramref name="stickToMouse"/>
    /// (the default) it follows the cursor like the game's own tooltips, otherwise it pins to the
    /// anchor. Call once the anchor is attached to a panel.
    /// </summary>
    public void Show(VisualElement anchor, bool stickToMouse = true)
    {
        FlushStats();
        try { UIManager.Get()?.AddTooltip(_data, anchor, false, stickToMouse, false); }
        catch { }
    }

    /// <summary>
    /// Take this tooltip back down. A no-op unless this tooltip is the one currently on top, so a mod
    /// never tears down a different tooltip (a game tooltip, or a nested child opened over this one)
    /// that it does not own.
    /// </summary>
    public void Hide()
    {
        try
        {
            var manager = UIManager.Get();
            if (manager != null && manager.GetActiveTooltipId() == _id)
                manager.RemoveActiveTooltip();
        }
        catch { }
    }

    private sealed class HoverState
    {
        public bool Registered;
        public string ShownId;
    }

    // Per-anchor hover state, keyed weakly so it dies with the element. Replaces a single shared
    // static field: each anchor tracks whether its handlers are wired and the id of the tooltip its
    // last enter showed, so a leave only removes that anchor's own tooltip.
    private static readonly ConditionalWeakTable<VisualElement, HoverState> HoverStates = new();

    /// <summary>
    /// Show a freshly-built tooltip while <paramref name="anchor"/> is hovered, and hide it on leave.
    /// <paramref name="build"/> runs on every hover, so the tooltip reflects current state. Return
    /// null from it to show nothing this time. Idempotent: wiring the same element more than once
    /// (e.g. after a screen rebuild) does not stack duplicate handlers.
    /// </summary>
    public static void OnHover(VisualElement anchor, Func<Tooltip> build, bool stickToMouse = true)
    {
        if (anchor == null || build == null)
            return;
        var state = HoverStates.GetOrCreateValue(anchor);
        if (state.Registered)
            return;
        state.Registered = true;
        try
        {
            anchor.RegisterCallback<PointerEnterEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerEnterEvent>>(
                (Action<PointerEnterEvent>)(_ =>
                {
                    Tooltip tooltip = null;
                    try { tooltip = build(); } catch { }
                    if (tooltip == null)
                        return;
                    state.ShownId = tooltip._id;
                    tooltip.Show(anchor, stickToMouse);
                })));
            anchor.RegisterCallback<PointerLeaveEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerLeaveEvent>>(
                (Action<PointerLeaveEvent>)(_ =>
                {
                    var shownId = state.ShownId;
                    state.ShownId = null;
                    if (shownId == null)
                        return;
                    try
                    {
                        var manager = UIManager.Get();
                        // Remove only the tooltip this anchor showed, and only while it is still the
                        // topmost one (a game tooltip, an adjacent anchor's tooltip, or a nested child
                        // opened on top is not ours) and not pinned (the game owns a pinned tooltip).
                        if (manager != null && manager.GetActiveTooltipId() == shownId && !manager.IsActiveTooltipPinned())
                            manager.RemoveActiveTooltip();
                    }
                    catch { }
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
