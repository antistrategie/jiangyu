using System;
using Il2CppInterop.Runtime.InteropTypes;

namespace Jiangyu.Game;

/// <summary>
/// Helpers for the game objects a mod handles, chiefly hook payloads cast from
/// <c>object</c> to a game type.
/// </summary>
public static class GameObjectExtensions
{
    /// <summary>
    /// Whether a game object is still usable. A hook hands you a transient wrapper;
    /// if you stash it and act on it a frame or a coroutine later, the underlying
    /// object may be gone, and calling into a dead wrapper crashes natively. This is
    /// the cheap guard to run first.
    ///
    /// <para>MENACE game objects (Entity, Actor, Skill, ...) are
    /// <c>Il2CppSystem.Object</c>-rooted, not <c>UnityEngine.Object</c>, so this is the
    /// IL2CPP collected/pointer check, not a Unity destroyed-check. It reports an object
    /// that Il2CppInterop has seen collected, but cannot prove one freed outside that
    /// tracking is dead. For a payload cached across frames, prefer re-resolving it
    /// through a live lookup (e.g. <c>Tactical.Actors()</c>) rather than trusting a
    /// stale reference.</para>
    /// </summary>
    public static bool IsAlive(this Il2CppObjectBase obj)
        => obj != null && !obj.WasCollected && obj.Pointer != IntPtr.Zero;
}
