using System;
using System.Collections.Generic;
using Il2CppMenace.UI;
using UnityEngine.UIElements;

namespace Jiangyu.Game;

/// <summary>
/// Describes where injected UI lands: which live screen or dialog to inject into,
/// optionally scoped to each element matching a selector, and where within that
/// scope to place the new element. Resolved fresh against the live tree on every
/// inject and refresh, so a target survives the screen being torn down and rebuilt.
/// </summary>
public sealed class UiTarget
{
    private enum Placement { Append, After, Before, Into }

    private readonly Func<VisualElement> _resolveRoot;
    private UiSelector _each;
    private Placement _placement = Placement.Append;
    private UiSelector _placementSelector;

    /// <summary>An overlay injection is stretched to fill the screen after insertion.</summary>
    internal bool IsOverlay { get; private set; }

    private UiTarget(Func<VisualElement> resolveRoot) => _resolveRoot = resolveRoot;

    /// <summary>Target the active screen when its concrete type is <typeparamref name="TScreen"/>.</summary>
    public static UiTarget Screen<TScreen>() where TScreen : UIScreen =>
        new(() =>
        {
            var screen = ActiveScreenAs<TScreen>();
            return screen != null ? RootOf(screen) : null;
        });

    /// <summary>Target whichever screen is currently active.</summary>
    public static UiTarget ActiveScreen() => new(ActiveScreenRoot);

    /// <summary>
    /// A full-screen overlay on the active screen, for a modal or dialog. The
    /// injected element is stretched to cover the screen, so a UXML backdrop styled
    /// <c>position: absolute</c> fills it. Hide and show the returned injection's
    /// element to open and close it.
    /// </summary>
    public static UiTarget Overlay() => new(ActiveScreenRoot) { IsOverlay = true };

    /// <summary>Target the open dialog when its concrete type is <typeparamref name="TDialog"/>.</summary>
    public static UiTarget Dialog<TDialog>() where TDialog : BaseDialog =>
        new(() =>
        {
            var manager = ManagerOrNull();
            BaseDialog dialog = null;
            try { dialog = manager != null ? manager.GetCurrentDialog() : null; }
            catch { dialog = null; }
            var typed = dialog != null ? dialog.TryCast<TDialog>() : null;
            return typed != null ? typed.TryCast<VisualElement>() : null;
        });

    /// <summary>Inject once into every element matching <paramref name="selector"/> under the root.</summary>
    public UiTarget Each(UiSelector selector)
    {
        _each = selector;
        return this;
    }

    /// <summary>Place the injected element immediately after the element matching <paramref name="anchor"/>.</summary>
    public UiTarget After(UiSelector anchor)
    {
        _placement = Placement.After;
        _placementSelector = anchor;
        return this;
    }

    /// <summary>Place the injected element immediately before the element matching <paramref name="anchor"/>.</summary>
    public UiTarget Before(UiSelector anchor)
    {
        _placement = Placement.Before;
        _placementSelector = anchor;
        return this;
    }

    /// <summary>Append the injected element to the container matching <paramref name="container"/>.</summary>
    public UiTarget AppendTo(UiSelector container)
    {
        _placement = Placement.Into;
        _placementSelector = container;
        return this;
    }

    // Resolve the live insertion sites. Empty when the root is not present (screen
    // closed), the scope selector matches nothing, or an anchor is missing.
    internal IReadOnlyList<UiSite> ResolveSites()
    {
        var sites = new List<UiSite>();
        VisualElement root;
        try { root = _resolveRoot(); }
        catch { root = null; }
        if (root == null)
            return sites;

        var scopes = _each == null
            ? new List<VisualElement> { root }
            : UiTree.FindAll(root, _each);

        foreach (var scope in scopes)
        {
            if (TryResolveSite(scope, out var site))
                sites.Add(site);
        }
        return sites;
    }

    private bool TryResolveSite(VisualElement scope, out UiSite site)
    {
        site = default;
        if (scope == null)
            return false;

        switch (_placement)
        {
            case Placement.After:
            case Placement.Before:
            {
                var anchor = UiTree.FindFirst(scope, _placementSelector);
                var parent = UiTree.ParentOf(anchor);
                var anchorIndex = UiTree.IndexInParent(anchor);
                if (parent == null || anchorIndex < 0)
                    return false;
                var index = _placement == Placement.After ? anchorIndex + 1 : anchorIndex;
                site = new UiSite(parent, index, scope);
                return true;
            }
            case Placement.Into:
            {
                var container = UiTree.FindFirst(scope, _placementSelector);
                if (container == null)
                    return false;
                site = new UiSite(container, UiTree.ChildCount(container), scope);
                return true;
            }
            default:
                site = new UiSite(scope, UiTree.ChildCount(scope), scope);
                return true;
        }
    }

    private static UIManager ManagerOrNull()
    {
        try { return UIManager.Get(); }
        catch { return null; }
    }

    private static UIScreen ActiveScreenRaw()
    {
        var manager = ManagerOrNull();
        try { return manager != null ? manager.GetActiveScreen() : null; }
        catch { return null; }
    }

    private static TScreen ActiveScreenAs<TScreen>() where TScreen : UIScreen
    {
        var screen = ActiveScreenRaw();
        try { return screen != null ? screen.TryCast<TScreen>() : null; }
        catch { return null; }
    }

    private static VisualElement RootOf(UIScreen screen)
    {
        try { return screen.GetRootElement(); }
        catch { return null; }
    }

    private static VisualElement ActiveScreenRoot()
    {
        var screen = ActiveScreenRaw();
        return screen != null ? RootOf(screen) : null;
    }
}

/// <summary>A resolved insertion point: insert at <see cref="Index"/> under <see cref="Parent"/>.</summary>
internal readonly struct UiSite
{
    internal VisualElement Parent { get; }
    internal int Index { get; }

    /// <summary>The <c>Each</c> match this site belongs to (the root when unscoped), passed to the bind callback.</summary>
    internal VisualElement Scope { get; }

    internal UiSite(VisualElement parent, int index, VisualElement scope)
    {
        Parent = parent;
        Index = index;
        Scope = scope;
    }
}
