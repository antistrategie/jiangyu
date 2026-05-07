using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Jiangyu.Loader.Templates;

// IL2CPP runtime helpers for the patch applier: subtype-cast through
// IL2CPP-stripped CIL, assignability checks that consult the native
// runtime, and fresh-instance allocation for composite/handler value
// construction. Carved out into a partial because the runtime-level
// IL2CPP wrangling is conceptually independent from the patch
// orchestration in the parent file and benefits from a small, focused
// surface.
internal sealed partial class TemplatePatchApplier
{
    // Subtype short-name → resolved Il2CppInterop wrapper type. Filled
    // lazily on first descent through a polymorphic destination.
    private static readonly Dictionary<string, Type> SubtypeResolutionCache = new(StringComparer.Ordinal);

    // Cached per-type lookups for IL2CPP allocation. Filled lazily on
    // first composite construction of each wrapper type. Plain
    // Dictionary is fine: template apply runs from JiangyuMod's
    // scene-load coroutine on the Unity main thread, so reads and
    // writes are serialised. A null entry in Il2CppIntPtrCtorCache is
    // the cached "no (IntPtr) ctor" sentinel; reuse it on subsequent
    // lookups instead of re-resolving.
    private static readonly Dictionary<Type, IntPtr> Il2CppClassPtrCache = new();
    private static readonly Dictionary<Type, ConstructorInfo> Il2CppIntPtrCtorCache = new();
    private static readonly Type[] IntPtrCtorSignature = new[] { typeof(IntPtr) };

    /// <summary>
    /// Cast an Il2CppInterop wrapper to a concrete subtype named by the
    /// modder via <c>type="<i>X</i>"</c> on a descent block. The wrapper
    /// returned by indexing a <c>List&lt;AbstractBase&gt;</c> reports the
    /// base type and exposes only the base's own members, so reflection
    /// can't see subclass fields like <c>AddSkill.ShowHUDText</c>. The cast
    /// goes through <see cref="Il2CppObjectBase.Cast{T}"/> reflectively
    /// because <c>T</c> is only known at runtime.
    /// </summary>
    private static bool TryCastToSubtype(object element, string subtypeShortName, out object cast, out string error)
    {
        cast = null!;
        error = null!;

        if (element is not Il2CppObjectBase il2cpp)
        {
            error = $"element type {element.GetType().FullName} is not an Il2CppObjectBase.";
            return false;
        }

        var concreteType = ResolveIl2CppSubtype(element.GetType(), subtypeShortName);
        if (concreteType == null)
        {
            error = $"no Il2Cpp wrapper type named '{subtypeShortName}' in the wrapper assembly.";
            return false;
        }

        if (!IsAssignableFromIl2Cpp(element.GetType(), concreteType))
        {
            error = $"'{subtypeShortName}' (full name '{concreteType.FullName}') "
                + $"does not derive from '{element.GetType().FullName}'.";
            return false;
        }

        if (!TryIl2CppCast(il2cpp, concreteType, out cast, out var castError))
        {
            error = castError;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Reflective <c>Il2CppObjectBase.Cast&lt;T&gt;</c> wrapper. The
    /// generic method has to be constructed per target type because T
    /// is only known at runtime; the cast itself is a wrapper-only
    /// conversion (same native pointer underneath) so the result
    /// reports the concrete type and exposes its members. Centralised
    /// because three call sites need it: subtype-cast in descent,
    /// type-coerce in ApplyAndVerify, and same-type-rewrap in
    /// TryConstructComposite after polymorphic factory return.
    /// </summary>
    private static bool TryIl2CppCast(Il2CppObjectBase wrapper, Type targetType, out object cast, out string error)
    {
        cast = null;
        try
        {
            var castMethod = typeof(Il2CppObjectBase)
                .GetMethod(nameof(Il2CppObjectBase.Cast))!
                .MakeGenericMethod(targetType);
            cast = castMethod.Invoke(wrapper, null)!;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Cast<{targetType.FullName}> threw: {(ex.InnerException ?? ex).Message}";
            return false;
        }
    }

    /// <summary>
    /// Assignability check that works for IL2CPP-wrapped interface types.
    /// Il2CppInterop wraps interfaces as plain classes (extending
    /// <c>Il2CppObjectBase</c>) and strips the implements relationships
    /// from concrete-class CIL, so <see cref="Type.IsAssignableFrom"/>
    /// returns false for cases like
    /// <c>ITacticalCondition.IsAssignableFrom(MoraleStateCondition)</c>.
    /// We try managed reflection first (fast, covers the class-inheritance
    /// case) then fall back to the native IL2CPP runtime check, which
    /// consults the unstripped type metadata table and recognises interface
    /// impls correctly.
    /// </summary>
    private static bool IsAssignableFromIl2Cpp(Type targetType, Type candidateType)
    {
        if (targetType.IsAssignableFrom(candidateType))
            return true;

        // Both wrapper types must descend from Il2CppObjectBase to ask the
        // native runtime; pure managed types fall through with false.
        if (!typeof(Il2CppObjectBase).IsAssignableFrom(targetType))
            return false;
        if (!typeof(Il2CppObjectBase).IsAssignableFrom(candidateType))
            return false;

        try
        {
            var targetPtr = Il2CppClassPointerStore.GetNativeClassPointer(targetType);
            var candidatePtr = Il2CppClassPointerStore.GetNativeClassPointer(candidateType);
            if (targetPtr == IntPtr.Zero || candidatePtr == IntPtr.Zero)
                return false;
            return IL2CPP.il2cpp_class_is_assignable_from(targetPtr, candidatePtr);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Find the Il2CppInterop wrapper type for <paramref name="shortName"/>,
    /// preferring the same namespace as <paramref name="elementType"/>. The
    /// wrapper assembly is what the path-walked element already lives in,
    /// so almost every game subtype is in the same namespace as its base.
    /// Falls back to a global short-name search if the same-namespace lookup
    /// finds nothing: covers cross-namespace subclassing cases. Returns
    /// null when no candidate matches.
    /// </summary>
    internal static Type ResolveIl2CppSubtype(Type elementType, string shortName)
    {
        var ns = elementType.Namespace ?? string.Empty;
        var cacheKey = ns + "::" + shortName;
        if (SubtypeResolutionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Same-namespace lookup first. Wrap in try/catch so a partial-load
        // assembly doesn't bypass the global fallback. Match by name AND
        // assignability: otherwise an unrelated same-namespace type (e.g.
        // SkillGroup in the same namespace as SkillEventHandlerTemplate)
        // wins the fast path and the caller sees a misleading "type X does
        // not derive from base" error downstream.
        Type same = null;
        try
        {
            same = elementType.Assembly
                .GetTypes()
                .FirstOrDefault(t => t.Name == shortName
                    && t.Namespace == ns
                    && IsAssignableFromIl2Cpp(elementType, t));
        }
        catch { /* fall through */ }

        if (same != null)
        {
            SubtypeResolutionCache[cacheKey] = same;
            return same;
        }

        // Fall back to all loaded assemblies: match short name + assignable
        // to the element type. Slower but covers types declared elsewhere.
        Type anywhere = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }

            foreach (var candidate in types)
            {
                if (candidate.Name != shortName) continue;
                if (!IsAssignableFromIl2Cpp(elementType, candidate)) continue;
                anywhere = candidate;
                break;
            }
            if (anywhere != null) break;
        }

        SubtypeResolutionCache[cacheKey] = anywhere;
        return anywhere;
    }

    private static IntPtr ResolveIl2CppClassPtr(Type t)
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

    private static ConstructorInfo ResolveIntPtrCtor(Type t)
    {
        return t.GetConstructor(IntPtrCtorSignature);
    }

    // Allocates an IL2CPP object for an Il2CppInterop wrapper type
    // (descendant of Il2CppObjectBase) and runs the type's IL2CPP-side
    // parameterless ctor so default-state invariants are established before
    // composite field writes overwrite individual members.
    //
    // Steps:
    //  1. il2cpp_object_new on the resolved native class pointer: allocates
    //     and zero-fills the IL2CPP object, sets up the vtable.
    //  2. il2cpp_runtime_object_init: invokes the type's parameterless
    //     instance ctor on the IL2CPP runtime so any defaults (backing
    //     lists, default flags) are populated. Best-effort: if no
    //     parameterless ctor exists or the ctor throws, we proceed against
    //     the zero-initialised object. Fields the modder authored will be
    //     overwritten next, and unauthored fields keep their zero defaults.
    //  3. Wrap with the generated managed (IntPtr) ctor so the result is a
    //     usable Il2CppObjectBase wrapper.
    private static bool TryAllocateIl2CppInstance(Type wrapperType, out object instance, out string error)
    {
        instance = null;
        error = null;

        if (!Il2CppClassPtrCache.TryGetValue(wrapperType, out var classPtr))
        {
            classPtr = ResolveIl2CppClassPtr(wrapperType);
            Il2CppClassPtrCache[wrapperType] = classPtr;
        }

        if (classPtr == IntPtr.Zero)
        {
            error = $"Il2CppClassPointerStore<{wrapperType.FullName}>.NativeClassPtr not found.";
            return false;
        }

        if (!Il2CppIntPtrCtorCache.TryGetValue(wrapperType, out var ctor))
        {
            ctor = ResolveIntPtrCtor(wrapperType);
            Il2CppIntPtrCtorCache[wrapperType] = ctor;
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
}
