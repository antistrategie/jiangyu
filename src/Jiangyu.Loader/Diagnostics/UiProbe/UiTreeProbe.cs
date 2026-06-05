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
        return BuildDump(sceneTag, screen, dialog, ConcreteName(screen), ConcreteName(dialog));
    }

    private static UiDump BuildDump(string sceneTag, UIScreen screen, BaseDialog dialog, string screenName, string dialogName)
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

        dump.NodeCount = nodes;
        dump.Truncated = nodes >= MaxNodes;
        return dump;
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
    }

    private sealed class UiNode
    {
        public string Type { get; set; }
        public string Name { get; set; }
        public string Text { get; set; }
        public List<string> Classes { get; set; }
        public List<UiNode> Children { get; set; }
    }
}
