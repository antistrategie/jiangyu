using System.Reflection;
using Il2CppInterop.Runtime;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Allocates an IL2CPP object for an Il2CppInterop wrapper type
/// (descendant of <c>Il2CppObjectBase</c>) and runs the type's IL2CPP-side
/// parameterless ctor so default-state invariants are established before
/// composite field writes overwrite individual members.
///
/// <para>Steps:
/// <list type="number">
///   <item><c>il2cpp_object_new</c> on the resolved native class pointer:
///     allocates and zero-fills the IL2CPP object, sets up the vtable.</item>
///   <item><c>il2cpp_runtime_object_init</c>: invokes the type's
///     parameterless instance ctor on the IL2CPP runtime so any defaults
///     (backing lists, default flags) are populated. Best-effort: if no
///     parameterless ctor exists or the ctor throws, we proceed against the
///     zero-initialised object. Fields the modder authored will be
///     overwritten next, and unauthored fields keep their zero defaults.</item>
///   <item>Wrap with the generated managed <c>(IntPtr)</c> ctor so the
///     result is a usable <c>Il2CppObjectBase</c> wrapper.</item>
/// </list></para>
/// </summary>
internal static class Il2CppInstanceAllocator
{
    // Cached per-type lookups. Filled lazily on first composite construction
    // of each wrapper type. Plain Dictionary is fine: template apply runs
    // from JiangyuMod's scene-load coroutine on the Unity main thread, so
    // reads and writes are serialised. A null entry in Il2CppIntPtrCtorCache
    // is the cached "no (IntPtr) ctor" sentinel; reuse it on subsequent
    // lookups instead of re-resolving.
    private static readonly Dictionary<Type, IntPtr> ClassPtrCache = new();
    private static readonly Dictionary<Type, ConstructorInfo> IntPtrCtorCache = new();
    private static readonly Type[] IntPtrCtorSignature = new[] { typeof(IntPtr) };

    public static bool TryAllocate(Type wrapperType, out object instance, out string error)
    {
        instance = null;
        error = null;

        if (!ClassPtrCache.TryGetValue(wrapperType, out var classPtr))
        {
            classPtr = ResolveClassPtr(wrapperType);
            ClassPtrCache[wrapperType] = classPtr;
        }

        if (classPtr == IntPtr.Zero)
        {
            error = $"Il2CppClassPointerStore<{wrapperType.FullName}>.NativeClassPtr not found.";
            return false;
        }

        if (!IntPtrCtorCache.TryGetValue(wrapperType, out var ctor))
        {
            ctor = wrapperType.GetConstructor(IntPtrCtorSignature);
            IntPtrCtorCache[wrapperType] = ctor;
        }

        if (ctor == null)
        {
            error = $"{wrapperType.FullName} has no (IntPtr) constructor; cannot wrap a fresh IL2CPP allocation.";
            return false;
        }

        var instancePtr = IL2CPP.il2cpp_object_new(classPtr);
        if (instancePtr == IntPtr.Zero)
        {
            error = $"il2cpp_object_new returned null for {wrapperType.FullName}.";
            return false;
        }

        // Best-effort IL2CPP-side ctor. Some wrapper types (pure data shells
        // with no parameterless .ctor on the native side) will throw here;
        // that's acceptable because authored field writes follow. Swallowing
        // matches the previous "skip ctor" behaviour as a fallback while
        // preserving correct init for the common case.
        try
        {
            IL2CPP.il2cpp_runtime_object_init(instancePtr);
        }
        catch
        {
            // Intentionally ignored; see comment above.
        }

        try
        {
            instance = ctor.Invoke(new object[] { instancePtr });
            return true;
        }
        catch (Exception ex)
        {
            error = $"(IntPtr) ctor invocation threw: {ex.InnerException?.Message ?? ex.Message}";
            return false;
        }
    }

    public static object TryAllocateOrNull(Type wrapperType)
        => TryAllocate(wrapperType, out var instance, out _) ? instance : null;

    private static IntPtr ResolveClassPtr(Type t)
    {
        try
        {
            var storeType = typeof(Il2CppClassPointerStore<>).MakeGenericType(t);
            var field = storeType.GetField(
                "NativeClassPtr",
                BindingFlags.Public | BindingFlags.Static);
            if (field == null)
                return IntPtr.Zero;
            var raw = field.GetValue(null);
            return raw == null ? IntPtr.Zero : (IntPtr)raw;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
