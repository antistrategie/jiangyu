using Il2CppInterop.Runtime.InteropTypes;

namespace Jiangyu.Loader.Templates;

// Polymorphic-cast bridge for descent into IL2CPP-typed elements. The
// generic IL2CPP reflection (cast, allocation, subtype resolution,
// assignability) lives under Templates/Il2Cpp/. This file keeps the one
// applier-shaped wrapper that ties them together for the `type="Subtype"`
// directive.
internal sealed partial class TemplatePatchApplier
{
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

        var concreteType = Il2CppSubtypeResolver.Resolve(element.GetType(), subtypeShortName);
        if (concreteType == null)
        {
            error = $"no Il2Cpp wrapper type named '{subtypeShortName}' in the wrapper assembly.";
            return false;
        }

        if (!Il2CppTypeAssignability.IsAssignableFromIl2Cpp(element.GetType(), concreteType))
        {
            error = $"'{subtypeShortName}' (full name '{concreteType.FullName}') "
                + $"does not derive from '{element.GetType().FullName}'.";
            return false;
        }

        if (!Il2CppReflectiveCast.TryCast(il2cpp, concreteType, out cast, out var castError))
        {
            error = castError;
            return false;
        }
        return true;
    }
}
