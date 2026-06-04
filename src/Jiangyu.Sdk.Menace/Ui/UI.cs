using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Jiangyu.Sdk;
using UnityEngine.UIElements;

namespace Jiangyu.Game;

/// <summary>
/// Adds mod UI into the game's live screens and dialogs. Injected elements join the
/// game's own UI Toolkit panel, so the game's stylesheets cascade to them: give an
/// element the game's USS class names (discover them with the Studio UI inspector,
/// or copy them off a neighbour with <see cref="UiElementExtensions.MatchStyle"/>)
/// and it is styled like native UI.
///
/// <para>Author the element as a UXML asset bundled with the mod (an <c>.uxml</c>
/// under <c>Assets/UI/</c>, with its USS linked by a <c>&lt;Style&gt;</c> tag) and
/// pass its name or the loaded <see cref="VisualTreeAsset"/>, or build it from a
/// callback. A <see cref="UiTarget"/> says which screen and where. The returned
/// <see cref="UiInjection"/> is re-applied automatically when the screen is rebuilt,
/// and <see cref="UiInjection.Refresh"/> rebuilds it on demand after the data behind
/// it changes.</para>
/// </summary>
public static class UI
{
    private static readonly List<RegisteredInjection> Injections = new();
    private static int _seq;
    private static Func<Assembly, string, VisualTreeAsset> _uxmlResolver = static (_, _) => null;

    /// <summary>Resolve a bundled UXML asset by calling assembly and name. Bound by the loader at startup.</summary>
    public static void BindUxmlResolver(Func<Assembly, string, VisualTreeAsset> resolver) =>
        _uxmlResolver = resolver ?? (static (_, _) => null);

    /// <summary>Inject the bundled UXML named <paramref name="uxml"/> (from the calling mod's assets) at <paramref name="target"/>.</summary>
    public static UiInjection Inject(UiTarget target, string uxml, Action<VisualElement> bind = null) =>
        Inject(target, ResolveUxml(uxml), bind);

    /// <summary>Inject a loaded <paramref name="uxml"/> asset at <paramref name="target"/>.</summary>
    public static UiInjection Inject(UiTarget target, VisualTreeAsset uxml, Action<VisualElement> bind = null) =>
        Register(target, CloneBuilder(uxml), Ignore(bind));

    /// <summary>Inject one element built by <paramref name="build"/> at <paramref name="target"/>.</summary>
    public static UiInjection Inject(UiTarget target, Func<VisualElement> build, Action<VisualElement> bind = null) =>
        Register(target, _ => build(), Ignore(bind));

    /// <summary>Inject the bundled UXML once per scoped match. <paramref name="bind"/> gets the new element and its scope.</summary>
    public static UiInjection InjectEach(UiTarget target, string uxml, Action<VisualElement, VisualElement> bind = null) =>
        InjectEach(target, ResolveUxml(uxml), bind);

    /// <summary>Inject a loaded <paramref name="uxml"/> asset once per scoped match.</summary>
    public static UiInjection InjectEach(UiTarget target, VisualTreeAsset uxml, Action<VisualElement, VisualElement> bind = null) =>
        Register(target, CloneBuilder(uxml), bind);

    /// <summary>Inject a built element once per scoped match. <paramref name="build"/> and <paramref name="bind"/> get the scope.</summary>
    public static UiInjection InjectEach(UiTarget target, Func<VisualElement, VisualElement> build, Action<VisualElement, VisualElement> bind = null) =>
        Register(target, build, bind);

    /// <summary>The first descendant of <paramref name="root"/> matching <paramref name="selector"/>, or null.</summary>
    public static VisualElement Find(VisualElement root, UiSelector selector) => UiTree.FindFirst(root, selector);

    /// <summary>Every descendant of <paramref name="root"/> matching <paramref name="selector"/>.</summary>
    public static IReadOnlyList<VisualElement> FindAll(VisualElement root, UiSelector selector) => UiTree.FindAll(root, selector);

    /// <summary>Whether any injection is registered. The loader skips its driver when false.</summary>
    internal static bool HasInjections => Injections.Count > 0;

    // Re-apply every live injection. The loader calls this when the active screen
    // changes, so an injection re-lands after the game tears down and rebuilds a
    // screen. Each injection skips sites it already occupies, so this is idempotent.
    // Iterates a snapshot: a bind callback may Remove an injection mid-pass (which
    // mutates Injections), and a removed injection's Reapply early-returns anyway.
    internal static void ReapplyAll()
    {
        foreach (var injection in Injections.ToArray())
            injection.Reapply();
    }

    internal static void Unregister(RegisteredInjection registered) => Injections.Remove(registered);

    private static UiInjection Register(UiTarget target, Func<VisualElement, VisualElement> build, Action<VisualElement, VisualElement> bind)
    {
        if (target == null || build == null)
            return new UiInjection(null);

        var registered = new RegisteredInjection(target, build, bind, $"jiangyu-ui-{_seq++}");
        Injections.Add(registered);
        registered.Reapply();
        return new UiInjection(registered);
    }

    private static Action<VisualElement, VisualElement> Ignore(Action<VisualElement> bind) =>
        bind == null ? null : (element, _) => bind(element);

    private static Func<VisualElement, VisualElement> CloneBuilder(VisualTreeAsset uxml) => _ =>
    {
        if (uxml == null)
            return null;
        try { return uxml.Instantiate(); }
        catch (Exception ex) { Log.Warn($"UI inject: instantiating UXML failed: {ex.Message}"); return null; }
    };

    // Resolve a bundled UXML from the calling mod's assets. Walks the stack for the
    // first frame whose assembly the loader maps to a mod that owns a UXML of that
    // name, so the mod never names another mod's asset. Resolved eagerly here while
    // the mod is on the stack, since the clone runs later off the loader's driver.
    private static VisualTreeAsset ResolveUxml(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        const int maxFrames = 16;
        for (var depth = 2; depth < 2 + maxFrames; depth++)
        {
            var method = new StackFrame(depth, needFileInfo: false).GetMethod();
            if (method == null)
                break;
            var assembly = method.DeclaringType?.Assembly;
            if (assembly == null)
                continue;
            VisualTreeAsset uxml;
            try { uxml = _uxmlResolver(assembly, name); }
            catch { uxml = null; }
            if (uxml != null)
                return uxml;
        }

        Log.Warn($"UI inject: no bundled UXML named '{name}' in the calling mod's assets. Bundle a .uxml with that name under Assets/UI/.");
        return null;
    }
}

// One injection's live state: its target, how to build the element, and the
// elements it currently has in the tree. Tagged with a unique marker class so a
// re-apply can tell whether it already occupies a site.
internal sealed class RegisteredInjection
{
    private readonly UiTarget _target;
    private readonly Func<VisualElement, VisualElement> _build;
    private readonly Action<VisualElement, VisualElement> _bind;
    private readonly string _marker;
    private readonly List<VisualElement> _injected = new();
    private bool _removed;

    internal RegisteredInjection(UiTarget target, Func<VisualElement, VisualElement> build, Action<VisualElement, VisualElement> bind, string marker)
    {
        _target = target;
        _build = build;
        _bind = bind;
        _marker = marker;
    }

    internal void Reapply()
    {
        if (_removed)
            return;
        // Drop elements that died or were detached (their parent subtree was rebuilt),
        // so a rebuilt site is re-injected and the tracking list doesn't accumulate orphans.
        _injected.RemoveAll(static e => !e.IsAlive() || UiTree.ParentOf(e) == null);

        foreach (var site in _target.ResolveSites())
        {
            var parent = site.Parent;
            if (parent == null || OccupiedBySelf(parent))
                continue;

            VisualElement element;
            try { element = _build(site.Scope); }
            catch (Exception ex) { Log.Warn($"UI inject build threw: {ex.Message}"); continue; }
            if (element == null)
                continue;

            try { element.AddToClassList(_marker); } catch { }
            if (!Insert(parent, site.Index, element))
                continue;
            if (_target.IsOverlay)
                UiTree.StretchFull(element);

            try { _bind?.Invoke(element, site.Scope); }
            catch (Exception ex) { Log.Warn($"UI inject bind threw: {ex.Message}"); }
            _injected.Add(element);
        }
    }

    internal void RemoveInjected()
    {
        foreach (var element in _injected)
            if (element.IsAlive())
                try { element.RemoveFromHierarchy(); } catch { }
        _injected.Clear();
    }

    internal void Dispose()
    {
        _removed = true;
        RemoveInjected();
    }

    private bool OccupiedBySelf(VisualElement parent)
    {
        var count = UiTree.ChildCount(parent);
        for (var i = 0; i < count; i++)
            if (UiTree.HasClass(UiTree.ChildAt(parent, i), _marker))
                return true;
        return false;
    }

    private static bool Insert(VisualElement parent, int index, VisualElement element)
    {
        var count = UiTree.ChildCount(parent);
        var at = index < 0 ? 0 : index > count ? count : index;
        try { parent.Insert(at, element); return true; }
        catch (Exception ex) { Log.Warn($"UI insert failed: {ex.Message}"); return false; }
    }
}
