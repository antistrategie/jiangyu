using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Jiangyu.Loader.Diagnostics;

/// <summary>
/// Resolves the concrete IL2CPP class name of a live object. A wrapper pulled from a
/// <c>List&lt;AbstractBase&gt;</c> reports the abstract base from <c>GetType().Name</c>
/// (e.g. <c>SkillEventHandlerTemplate</c>) even when the underlying object is a concrete
/// subclass like <c>AddSkill</c>. Asking IL2CPP directly via
/// <c>il2cpp_object_get_class</c> returns the real runtime class name. Cached by class
/// pointer so each unique concrete type costs one P/Invoke once.
/// </summary>
internal static class Il2CppTypeName
{
    private static readonly Dictionary<IntPtr, string> _cache = new();

    /// <summary>The concrete IL2CPP class name, or null when it cannot be resolved.</summary>
    public static string Resolve(Il2CppObjectBase value)
    {
        IntPtr objectPointer;
        try { objectPointer = value.Pointer; }
        catch { return null; }
        if (objectPointer == IntPtr.Zero) return null;

        IntPtr klass;
        try { klass = IL2CPP.il2cpp_object_get_class(objectPointer); }
        catch { return null; }
        if (klass == IntPtr.Zero) return null;

        if (_cache.TryGetValue(klass, out var cached))
            return cached;

        string name;
        try
        {
            var namePtr = IL2CPP.il2cpp_class_get_name(klass);
            name = namePtr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(namePtr);
        }
        catch
        {
            name = null;
        }

        _cache[klass] = name;
        return name;
    }
}
