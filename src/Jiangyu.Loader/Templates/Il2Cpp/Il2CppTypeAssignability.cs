using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Assignability check that works for IL2CPP-wrapped interface types.
/// Il2CppInterop wraps interfaces as plain classes (extending
/// <c>Il2CppObjectBase</c>) and strips the implements relationships from
/// concrete-class CIL, so <see cref="Type.IsAssignableFrom"/> returns false
/// for cases like <c>ITacticalCondition.IsAssignableFrom(MoraleStateCondition)</c>.
/// We try managed reflection first (fast, covers the class-inheritance case)
/// then fall back to the native IL2CPP runtime check, which consults the
/// unstripped type metadata table and recognises interface impls correctly.
///
/// <para>This is the runtime mirror of Core's <c>Il2CppMetadataSupplement</c>
/// offline interface-implementation table. Both layers answer the same
/// question (does X implement Y?) against complementary data sources.</para>
/// </summary>
internal static class Il2CppTypeAssignability
{
    public static bool IsAssignableFromIl2Cpp(Type targetType, Type candidateType)
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
}
