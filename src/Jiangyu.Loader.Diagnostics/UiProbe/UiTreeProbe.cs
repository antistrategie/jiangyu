using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppMenace.UI;
using UnityEngine.UIElements;

namespace Jiangyu.Loader.Diagnostics.UiProbe;

/// <summary>
/// Captures the live UI Toolkit <see cref="VisualElement"/> tree of the active
/// <c>UIScreen</c> and any open dialog, so a modder can find the exact element to
/// attach injected UI to (e.g. the squad menu's leader header). Driven on demand by
/// the <c>ui.capture</c> bridge request and returned to the caller.
///
/// <para>Concrete element types come from the IL2CPP class (a wrapper's managed
/// type is only its static cast type), so the dump distinguishes a
/// <c>ProgressBar</c> or <c>SquaddieRow</c> from a plain <c>VisualElement</c>.</para>
/// </summary>
internal static class UiTreeProbe
{
    private const int MaxDepth = 18;
    private const int MaxNodes = 5000;

    /// <summary>
    /// Capture the live screen-plus-dialog tree of the active UIScreen and any open
    /// dialog, and return it. Driven by the <c>ui.capture</c> bridge request; must run
    /// on the Unity main thread. Returns null when no UI manager is up. The return type
    /// is <c>object</c> so the loader can hand it straight to the JSON serialiser.
    /// </summary>
    public static object CaptureCurrent(string sceneTag)
    {
        UIManager manager;
        try { manager = UIManager.Get(); }
        catch { return null; }
        if (manager == null)
            return null;

        UIScreen screen = null;
        BaseDialog dialog = null;
        try { screen = manager.GetActiveScreen(); } catch { /* none */ }
        try { dialog = manager.GetCurrentDialog(); } catch { /* none */ }
        return BuildDump(sceneTag, manager, screen, dialog, ConcreteName(screen), ConcreteName(dialog));
    }

    private static UiDump BuildDump(string sceneTag, UIManager manager, UIScreen screen, BaseDialog dialog, string screenName, string dialogName)
    {
        var dump = new UiDump
        {
            Timestamp = DateTimeOffset.UtcNow,
            SceneTag = sceneTag,
            ActiveScreen = screenName,
            CurrentDialog = dialogName,
        };

        var nodes = 0;
        VisualElement screenRoot = null;
        try { screenRoot = screen != null ? screen.GetRootElement() : null; } catch { /* none */ }
        if (screenRoot != null)
            dump.ScreenTree = Walk(screenRoot, 0, ref nodes);
        if (dialog != null)
            dump.DialogTree = Walk(dialog.TryCast<VisualElement>(), 0, ref nodes);

        dump.Tooltips = CaptureTooltips(manager, ref nodes);

        dump.NodeCount = nodes;
        dump.Truncated = nodes >= MaxNodes;
        return dump;
    }

    // The live tooltips, which render on their own overlay panel that the active-screen and
    // dialog roots above do not reach. UIManager keeps every open tooltip (pinned and the
    // nested children opened by hovering a row or a <link> inside a parent) in m_TooltipStack.
    // For each entry the trigger element (ElementWithTooltip) is the exact thing a child hangs
    // off, so the dump records it next to the tooltip's own content tree.
    private static List<UiTooltip> CaptureTooltips(UIManager manager, ref int nodes)
    {
        Il2CppSystem.Collections.Generic.Stack<TooltipStackEntry> stack;
        try { stack = manager.m_TooltipStack; }
        catch { return null; }
        if (stack == null)
            return null;

        TooltipStackEntry[] entries;
        try
        {
            var array = stack.ToArray();
            entries = new TooltipStackEntry[array.Length];
            for (var i = 0; i < array.Length; i++) entries[i] = array[i];
        }
        catch { return null; }

        var result = new List<UiTooltip>();
        foreach (var entry in entries)
        {
            if (entry == null || nodes >= MaxNodes) continue;
            var tip = new UiTooltip();
            try { tip.TooltipId = entry.TooltipId; } catch { }
            try { tip.IsPinned = entry.IsPinned; } catch { }

            // The element that owns/triggered this tooltip. Identity only (no child walk): for a
            // nested child this is the row/word/stat in the parent that carries the create-func or
            // <link>, which is the whole point of the capture.
            try
            {
                var trigger = entry.ElementWithTooltip;
                if (trigger != null)
                    tip.Trigger = Identity(trigger, ref nodes);
            }
            catch { }

            // The tooltip's own content. The Tooltip is an InterfaceElement (a VisualElement), so
            // walk it directly; fall back to its content container when the cast does not hold.
            try
            {
                var tooltip = entry.Tooltip;
                VisualElement root = null;
                try { root = tooltip?.TryCast<VisualElement>(); } catch { }
                if (root == null) try { root = tooltip?.GetContent(); } catch { }
                if (root != null)
                    tip.Tree = Walk(root, 0, ref nodes);
            }
            catch { }

            result.Add(tip);
        }
        return result.Count > 0 ? result : null;
    }

    // Identity of a single element without descending into its children: concrete type, name,
    // USS classes, and text (which preserves any <link=id> rich-text markup, so a link-word
    // trigger is visible in the dump). Counts against the global node cap like Walk does.
    private static UiNode Identity(VisualElement element, ref int nodes)
    {
        if (element == null || nodes >= MaxNodes) return null;
        nodes++;
        return new UiNode
        {
            Type = ConcreteName(element),
            Name = SafeName(element),
            Classes = ReadClasses(element),
            Text = ReadText(element),
            Style = ReadStyle(element),
        };
    }

    // Recursively capture an element's identity, USS classes, text, and children,
    // bounded by depth and a global node cap so a huge tree cannot run away.
    private static UiNode Walk(VisualElement element, int depth, ref int nodes)
    {
        if (element == null || depth > MaxDepth || nodes >= MaxNodes)
            return null;
        nodes++;

        var node = new UiNode
        {
            Type = ConcreteName(element),
            Name = SafeName(element),
            Classes = ReadClasses(element),
            Text = ReadText(element),
            Style = ReadStyle(element),
        };

        int childCount;
        try { childCount = element.childCount; }
        catch { childCount = 0; }

        for (var i = 0; i < childCount && nodes < MaxNodes; i++)
        {
            VisualElement child;
            try { child = element.ElementAt(i); }
            catch { continue; }
            var childNode = Walk(child, depth + 1, ref nodes);
            if (childNode != null)
                (node.Children ??= new List<UiNode>()).Add(childNode);
        }
        return node;
    }

    private static string SafeName(VisualElement element)
    {
        try { var n = element.name; return string.IsNullOrEmpty(n) ? null : n; }
        catch { return null; }
    }

    private static string ReadText(VisualElement element)
    {
        try
        {
            var text = element.TryCast<TextElement>();
            var value = text != null ? text.text : null;
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch { return null; }
    }

    private static List<string> ReadClasses(VisualElement element)
    {
        try
        {
            var classes = element.GetClassesForIteration();
            if (classes == null || classes.Count == 0)
                return null;
            var result = new List<string>(classes.Count);
            for (var i = 0; i < classes.Count; i++)
                result.Add(classes[i]);
            return result;
        }
        catch { return null; }
    }

    // Computed layout (the resolved pixel box) plus the key visual styles a modder
    // needs to match injected UI to the native look. Each read is guarded so a property
    // missing on this Unity build drops that one field rather than the whole node, and
    // transparent / zero values are omitted to keep the dump compact.
    private static Dictionary<string, object> ReadStyle(VisualElement element)
    {
        var style = new Dictionary<string, object>();

        try
        {
            var box = element.worldBound;
            // Guard every component: a NaN in any of x/y/w/h serialises to a bare NaN
            // token that System.Text.Json rejects, which would drop the whole dump.
            if (!float.IsNaN(box.x) && !float.IsNaN(box.y) && !float.IsNaN(box.width) && !float.IsNaN(box.height))
            {
                style["x"] = System.Math.Round(box.x, 1);
                style["y"] = System.Math.Round(box.y, 1);
                style["w"] = System.Math.Round(box.width, 1);
                style["h"] = System.Math.Round(box.height, 1);
            }
        }
        catch { }

        try
        {
            var rs = element.resolvedStyle;
            if (rs != null)
            {
                try { var c = rs.backgroundColor; if (c.a > 0.004f) style["bg"] = Col(c); } catch { }
                try { var c = rs.color; if (c.a > 0.004f) style["color"] = Col(c); } catch { }
                try { var fs = rs.fontSize; if (fs > 0.01f) style["fontSize"] = System.Math.Round(fs, 1); } catch { }
                try
                {
                    var bw = rs.borderTopWidth;
                    if (bw > 0.01f)
                    {
                        style["borderWidth"] = System.Math.Round(bw, 1);
                        var bc = rs.borderTopColor;
                        if (bc.a > 0.004f) style["borderColor"] = Col(bc);
                    }
                }
                catch { }
                try { style["flexDirection"] = rs.flexDirection.ToString(); } catch { }
                try { var op = rs.opacity; if (op < 0.999f) style["opacity"] = System.Math.Round(op, 2); } catch { }
                try { if (rs.display.ToString() == "None") style["display"] = "None"; } catch { }
                try
                {
                    var bg = rs.backgroundImage;
                    string image = null;
                    try { var sprite = bg.sprite; if (sprite != null) image = "sprite:" + sprite.name; } catch { }
                    if (image == null) try { var tex = bg.texture; if (tex != null) image = "texture:" + tex.name; } catch { }
                    if (image == null) try { var vec = bg.vectorImage; if (vec != null) image = "vector:" + vec.name; } catch { }
                    if (image != null)
                    {
                        style["bgImage"] = image;
                        try { var t = rs.unityBackgroundImageTintColor; if (t.r < 0.99f || t.g < 0.99f || t.b < 0.99f || t.a < 0.99f) style["bgTint"] = Col(t); } catch { }
                        try
                        {
                            var l = rs.unitySliceLeft; var tp = rs.unitySliceTop; var r = rs.unitySliceRight; var b = rs.unitySliceBottom;
                            if (l != 0 || tp != 0 || r != 0 || b != 0) style["slice"] = $"{l},{tp},{r},{b}";
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { }

        return style.Count > 0 ? style : null;
    }

    private static string Col(UnityEngine.Color c) =>
        $"rgba({(int)System.Math.Round(c.r * 255f)}, {(int)System.Math.Round(c.g * 255f)}, {(int)System.Math.Round(c.b * 255f)}, {System.Math.Round(c.a, 2)})";

    // The concrete IL2CPP class name of a wrapper, e.g. "ProgressBar"/"SquaddieRow",
    // not the static cast type. Null for a null object.
    private static string ConcreteName(Il2CppObjectBase obj)
    {
        if (obj == null || obj.Pointer == IntPtr.Zero)
            return null;
        try
        {
            var klass = IL2CPP.il2cpp_object_get_class(obj.Pointer);
            return Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
        }
        catch
        {
            try { return obj.GetType().Name; }
            catch { return null; }
        }
    }

    // Over the bridge this serialises camelCase and Studio reads it through the [RpcType]
    // mirror in Jiangyu.Studio.Rpc/Handlers/RpcHandlers.Bridge.cs (UiDump/UiNode). The
    // ActiveScreen..Children fields below are that contract; Timestamp/SceneTag are file-dump only.
    private sealed class UiDump
    {
        public DateTimeOffset Timestamp { get; set; }
        public string SceneTag { get; set; }
        public string ActiveScreen { get; set; }
        public string CurrentDialog { get; set; }
        public int NodeCount { get; set; }
        public bool Truncated { get; set; }
        public UiNode ScreenTree { get; set; }
        public UiNode DialogTree { get; set; }
        public List<UiTooltip> Tooltips { get; set; }
    }

    private sealed class UiTooltip
    {
        public string TooltipId { get; set; }
        public bool IsPinned { get; set; }
        public UiNode Trigger { get; set; }
        public UiNode Tree { get; set; }
    }

    private sealed class UiNode
    {
        public string Type { get; set; }
        public string Name { get; set; }
        public string Text { get; set; }
        public List<string> Classes { get; set; }
        public Dictionary<string, object> Style { get; set; }
        public List<UiNode> Children { get; set; }
    }
}
