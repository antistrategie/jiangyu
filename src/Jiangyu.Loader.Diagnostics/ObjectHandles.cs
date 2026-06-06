using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;

namespace Jiangyu.Loader.Diagnostics;

/// <summary>
/// A bounded registry of live game objects the verb runner has handed back, so a verb
/// result can be passed as the argument to another verb. A live object (an Operation, a
/// Planet, a Skill, ...) has no stable id to look it up by, so <see cref="VerbRunner"/>
/// serialises it with a handle from here and resolves a <c>{ref:"handle"}</c> argument
/// back to the same instance.
///
/// <para>Handles are valid only within a scene: stale instances from a previous mission
/// would fault when touched, so <see cref="Clear"/> is called on scene load. The oldest
/// handles are evicted past <see cref="Capacity"/>. All access is on the bridge's main
/// thread, so no locking is needed.</para>
/// </summary>
internal static class ObjectHandles
{
    private const int Capacity = 512;

    private static readonly Dictionary<string, Il2CppObjectBase> _byHandle = new();
    private static readonly Queue<string> _order = new();
    private static int _next;

    /// <summary>Register a live object and return its handle.</summary>
    public static string Register(Il2CppObjectBase value)
    {
        var handle = "h" + ++_next;
        _byHandle[handle] = value;
        _order.Enqueue(handle);
        while (_order.Count > Capacity)
            _byHandle.Remove(_order.Dequeue());
        return handle;
    }

    /// <summary>Resolve a handle to the object it names, or false if it has been evicted or cleared.</summary>
    public static bool TryGet(string handle, out Il2CppObjectBase value)
    {
        if (handle != null)
            return _byHandle.TryGetValue(handle, out value);
        value = null;
        return false;
    }

    /// <summary>Drop every handle. Called on scene load so a handle never resolves to a stale instance.</summary>
    public static void Clear()
    {
        _byHandle.Clear();
        _order.Clear();
    }
}
