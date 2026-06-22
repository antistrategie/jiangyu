using System;
using System.Reflection;
using Jiangyu.Game.Input;
using UnityEngine;

namespace Jiangyu.Loader.Sdk.Input;

// Backs Jiangyu.Game.Input.Hotkeys. Translates a (key, edge) registration into a per-frame
// signal over the dispatch core, and attributes it to the mod whose code owns the handler so
// the loader can drop a mod's hotkeys on unload, the way it stops a mod's coroutines and
// removes its patches. Register bakes the legacy-input check into the entry's signal, so the
// dispatch itself never sees a KeyCode and stays UnityEngine-free.
internal sealed class HotkeyRegistry : Hotkeys.IRegistrar
{
    private readonly HotkeyDispatch _dispatch;
    private readonly Func<Assembly, string> _modIdForAssembly;

    public HotkeyRegistry(HotkeyDispatch dispatch, Func<Assembly, string> modIdForAssembly)
    {
        _dispatch = dispatch;
        _modIdForAssembly = modIdForAssembly;
    }

    public IDisposable Register(Hotkeys.Edge edge, KeyCode key, Action handler, Func<bool> when)
        => _dispatch.Add(OwnerOf(handler), () => IsActive(edge, key), when, handler);

    /// <summary>Dispose every hotkey a mod registered. Called when the mod unloads or is
    /// quarantined.</summary>
    public void ClearMod(string modId) => _dispatch.ClearOwner(modId);

    // The mod that owns a handler is the mod whose assembly defines it: a handler is mod code,
    // and a lambda's closure class lives in the same assembly. Null (a handler from outside any
    // mod) leaves the registration ungrouped, living until the caller disposes it.
    private string OwnerOf(Action handler)
    {
        var assembly = handler?.Method?.DeclaringType?.Assembly;
        return assembly == null ? null : _modIdForAssembly?.Invoke(assembly);
    }

    // The only UnityEngine.Input touch. Legacy input module: GetKeyDown/Up/GetKey.
    private static bool IsActive(Hotkeys.Edge edge, KeyCode key) => edge switch
    {
        Hotkeys.Edge.Down => UnityEngine.Input.GetKeyDown(key),
        Hotkeys.Edge.Up => UnityEngine.Input.GetKeyUp(key),
        Hotkeys.Edge.Held => UnityEngine.Input.GetKey(key),
        _ => false,
    };
}
