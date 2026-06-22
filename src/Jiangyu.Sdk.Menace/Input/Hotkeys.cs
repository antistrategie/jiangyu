using System;
using UnityEngine;

namespace Jiangyu.Game.Input;

/// <summary>
/// Keyboard hotkeys for mods. Register a handler for a key and get back an
/// <see cref="IDisposable"/>; dispose it to unregister. The loader runs a single per-frame
/// poll and fans each press out to every registered handler, so a mod never writes its own
/// frame loop. Scope a hotkey by controlling the handle's lifetime (register when a panel
/// opens, dispose when it closes) or by passing a <c>when</c> predicate that gates firing.
/// </summary>
public static class Hotkeys
{
    /// <summary>Which key transition a handler fires on.</summary>
    internal enum Edge
    {
        /// <summary>The frame the key is first pressed.</summary>
        Down,

        /// <summary>The frame the key is released.</summary>
        Up,

        /// <summary>Every frame the key is held.</summary>
        Held,
    }

    /// <summary>Implemented by the loader; bound once at startup. Returns a handle whose
    /// disposal removes the registration.</summary>
    internal interface IRegistrar
    {
        IDisposable Register(Edge edge, KeyCode key, Action handler, Func<bool> when);
    }

    private static IRegistrar _registrar;

    internal static void BindRegistrar(IRegistrar registrar) => _registrar = registrar;

    /// <summary>Fire <paramref name="handler"/> the frame <paramref name="key"/> goes down.
    /// Pass <paramref name="when"/> to fire only while a condition holds.</summary>
    public static IDisposable OnKeyDown(KeyCode key, Action handler, Func<bool> when = null)
        => Register(Edge.Down, key, handler, when);

    /// <summary>Fire <paramref name="handler"/> the frame <paramref name="key"/> is released.</summary>
    public static IDisposable OnKeyUp(KeyCode key, Action handler, Func<bool> when = null)
        => Register(Edge.Up, key, handler, when);

    /// <summary>Fire <paramref name="handler"/> every frame <paramref name="key"/> is held down.</summary>
    public static IDisposable OnKeyHeld(KeyCode key, Action handler, Func<bool> when = null)
        => Register(Edge.Held, key, handler, when);

    private static IDisposable Register(Edge edge, KeyCode key, Action handler, Func<bool> when)
    {
        ArgumentNullException.ThrowIfNull(handler);
        // No registrar means no loader is hosting us (e.g. a unit test or a tool). Return a
        // no-op handle so a mod's OnInit doesn't fault when run outside the game.
        return _registrar?.Register(edge, key, handler, when) ?? NoopHandle.Instance;
    }

    private sealed class NoopHandle : IDisposable
    {
        public static readonly NoopHandle Instance = new();
        public void Dispose() { }
    }
}
