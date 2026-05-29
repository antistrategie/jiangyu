using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Jiangyu.Loader.Templates;

// Polymorphic-cast bridge for descent into IL2CPP-typed elements. The
// generic IL2CPP reflection (cast, allocation, subtype resolution,
// assignability) lives under Templates/Il2Cpp/. This file keeps the
// applier-shaped wrappers that tie them together for edit descent.
internal sealed partial class TemplatePatchApplier
{
    /// <summary>
    /// Cast a base-typed element wrapper to the wrapper for its actual live
    /// concrete type, read from the IL2CPP runtime. Backs no-type edit descent
    /// (<c>set "Field" index=N { ... }</c>): the subtype isn't authored, so the
    /// applier discovers it from the element itself and casts so the inner ops
    /// can address subclass members. Returns false (leaving the base wrapper in
    /// place) when the concrete type can't be resolved; callers treat that as
    /// best-effort.
    /// </summary>
    private static bool TryCastToLiveConcreteType(object element, Type collectionType, out object cast, out string error)
    {
        cast = null!;
        error = null!;

        if (element is not Il2CppObjectBase il2cpp)
        {
            error = $"element type {element.GetType().FullName} is not an Il2CppObjectBase.";
            return false;
        }

        string concreteShortName;
        try
        {
            var klass = IL2CPP.il2cpp_object_get_class(il2cpp.Pointer);
            if (klass == IntPtr.Zero)
            {
                error = "il2cpp_object_get_class returned null.";
                return false;
            }
            concreteShortName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
        }
        catch (Exception ex)
        {
            error = $"reading live element class name threw: {ex.Message}";
            return false;
        }

        if (string.IsNullOrEmpty(concreteShortName))
        {
            error = "could not read the live element's class name.";
            return false;
        }

        // Anchor subtype resolution on the collection's declared element type
        // so the short name disambiguates within that family.
        var baseElementType = Il2CppCollectionReflection.GetListElementType(collectionType)
            ?? Il2CppCollectionReflection.GetArrayElementType(collectionType)
            ?? element.GetType();

        var concreteType = Il2CppSubtypeResolver.Resolve(baseElementType, concreteShortName);
        if (concreteType == null)
        {
            error = $"no wrapper type for live element class '{concreteShortName}'.";
            return false;
        }

        // Already the concrete wrapper: nothing to do.
        if (concreteType == element.GetType())
        {
            cast = element;
            return true;
        }

        if (!Il2CppReflectiveCast.TryCast(il2cpp, concreteType, out cast, out var castError))
        {
            error = $"cast to live concrete type '{concreteShortName}' failed: {castError}";
            return false;
        }
        return true;
    }
}
