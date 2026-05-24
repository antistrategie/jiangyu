using Il2CppInterop.Runtime;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Native IL2CPP field-by-name lookup walking the class hierarchy.
/// <c>IL2CPP.il2cpp_class_get_field_from_name</c> only consults the class
/// passed in, so we walk to <c>il2cpp_class_get_parent</c> when the field
/// is declared on a base. Returns <see cref="IntPtr.Zero"/> if the field
/// is missing across the whole hierarchy.
/// </summary>
internal static class Il2CppFieldLookup
{
    public static IntPtr FindFieldInHierarchy(IntPtr klass, string fieldName)
    {
        var current = klass;
        while (current != IntPtr.Zero)
        {
            var field = IL2CPP.il2cpp_class_get_field_from_name(current, fieldName);
            if (field != IntPtr.Zero)
                return field;

            current = IL2CPP.il2cpp_class_get_parent(current);
        }
        return IntPtr.Zero;
    }
}
