using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Stores into IL2CPP arrays and value-type memory that pass every object
/// reference through the GC write barrier (<c>il2cpp_gc_wbarrier_set_field</c>).
/// The game runs Unity's incremental GC (<c>gc-max-time-slice</c> in
/// <c>boot.config</c>), which only rescans an object it has already marked
/// in the current cycle when a store into it goes through the barrier. A
/// reference stored without one, into an already-marked object, to an object
/// nothing else holds, is freed at the end of the cycle and the store is left
/// dangling. Il2CppInterop's array indexers and their <c>(T[])</c>
/// constructors write element pointers directly, so every array element
/// write the loader makes goes through here instead.
/// </summary>
internal static class Il2CppGcWrites
{
    private static readonly int ObjectHeaderSize = 2 * IntPtr.Size;
    private static readonly int ArrayDataOffset = 4 * IntPtr.Size;
    private const int FieldAttributeStatic = 0x0010;
    private const int MaxStructDepth = 16;

    /// <summary>
    /// True for the Il2CppInterop wrappers whose elements are IL2CPP objects
    /// (<see cref="Il2CppStringArray"/> and <c>Il2CppReferenceArray&lt;T&gt;</c>).
    /// Pure reflection, safe outside a live IL2CPP process.
    /// </summary>
    public static bool IsReferenceArrayWrapper(Type arrayType)
    {
        if (arrayType == typeof(Il2CppStringArray))
            return true;
        for (var current = arrayType; current != null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Il2CppReferenceArray<>))
                return true;
        }
        return false;
    }

    /// <summary>
    /// A new native array of <paramref name="length"/> elements, wrapped as
    /// <paramref name="arrayType"/>, allocated with the wrapper's own element
    /// class and with every slot left null.
    /// </summary>
    public static Il2CppObjectBase Allocate(Type arrayType, int length)
    {
        var ctor = arrayType.GetConstructor(new[] { typeof(long) })
            ?? throw new InvalidOperationException($"{arrayType.FullName} has no (long) constructor.");
        return (Il2CppObjectBase)ctor.Invoke(new object[] { (long)length });
    }

    /// <summary>
    /// Stores <paramref name="value"/> (a managed string, an Il2Cpp wrapper,
    /// or null) at <paramref name="index"/> of the native array.
    /// </summary>
    public static void SetElement(Il2CppObjectBase array, int index, object value)
        => SetElement(array.Pointer, index, ToNative(value));

    /// <summary>
    /// Stores the object at <paramref name="value"/> at <paramref name="index"/>
    /// of the native array. A reference element is type-checked against the
    /// array's element class, as a managed array store would be. A value-type
    /// element takes the unboxed copy of the boxed <paramref name="value"/>.
    /// </summary>
    public static unsafe void SetElement(IntPtr array, int index, IntPtr value)
    {
        var elementClass = CheckedElementClass(array, index, 1, out var elementSize);
        var slot = (byte*)array + ArrayDataOffset + (long)index * elementSize;

        if (IL2CPP.il2cpp_class_is_valuetype(elementClass))
        {
            if (value == IntPtr.Zero)
                throw new NullReferenceException("cannot store null into a value-type array element.");
            Buffer.MemoryCopy((void*)IL2CPP.il2cpp_object_unbox(value), slot, elementSize, elementSize);
            BarrierStructReferences(array, slot, elementClass, 0);
            return;
        }

        if (value != IntPtr.Zero
            && !IL2CPP.il2cpp_class_is_assignable_from(elementClass, IL2CPP.il2cpp_object_get_class(value)))
        {
            throw new ArrayTypeMismatchException(
                $"{IL2CPP.il2cpp_class_get_name_(IL2CPP.il2cpp_object_get_class(value))} cannot be stored "
                + $"in an array of {IL2CPP.il2cpp_class_get_name_(elementClass)}.");
        }

        IL2CPP.il2cpp_gc_wbarrier_set_field(array, (IntPtr)slot, value);
    }

    /// <summary>
    /// Copies <paramref name="count"/> elements between two native arrays of
    /// the same element layout, keeping the elements themselves (no string
    /// round-trip through managed memory).
    /// </summary>
    public static unsafe void CopyElements(
        IntPtr source, int sourceIndex, IntPtr destination, int destinationIndex, int count)
    {
        if (count <= 0)
            return;

        var sourceClass = CheckedElementClass(source, sourceIndex, count, out var sourceSize);
        var elementClass = CheckedElementClass(destination, destinationIndex, count, out var elementSize);
        var isValueType = IL2CPP.il2cpp_class_is_valuetype(elementClass);
        if (sourceSize != elementSize || IL2CPP.il2cpp_class_is_valuetype(sourceClass) != isValueType)
            throw new ArrayTypeMismatchException("source and destination arrays have different element layouts.");

        var from = (byte*)source + ArrayDataOffset + (long)sourceIndex * elementSize;
        var to = (byte*)destination + ArrayDataOffset + (long)destinationIndex * elementSize;
        for (var i = 0; i < count; i++, from += elementSize, to += elementSize)
        {
            if (isValueType)
            {
                Buffer.MemoryCopy(from, to, elementSize, elementSize);
                BarrierStructReferences(destination, to, elementClass, 0);
            }
            else
            {
                IL2CPP.il2cpp_gc_wbarrier_set_field(destination, (IntPtr)to, *(IntPtr*)from);
            }
        }
    }

    /// <summary>
    /// Re-stores every object reference inside the unboxed
    /// <paramref name="valueClass"/> value at <paramref name="address"/>
    /// through the write barrier, for a value that was just copied into
    /// <paramref name="owner"/> as raw bytes. Re-storing a slot's current
    /// value only records the write, so a word that is not a reference is
    /// left unchanged.
    /// </summary>
    public static unsafe void BarrierStructReferences(IntPtr owner, byte* address, IntPtr valueClass, int depth)
    {
        if (depth >= MaxStructDepth || IL2CPP.il2cpp_class_is_blittable(valueClass))
            return;

        var iterator = IntPtr.Zero;
        IntPtr field;
        while ((field = IL2CPP.il2cpp_class_get_fields(valueClass, ref iterator)) != IntPtr.Zero)
        {
            if ((IL2CPP.il2cpp_field_get_flags(field) & FieldAttributeStatic) != 0)
                continue;
            var fieldType = IL2CPP.il2cpp_field_get_type(field);
            if (fieldType == IntPtr.Zero)
                continue;
            var fieldClass = IL2CPP.il2cpp_class_from_il2cpp_type(fieldType);
            if (fieldClass == IntPtr.Zero)
                continue;

            // JIANGYU-CONTRACT: IL2CPP reports a value type's field offsets in
            // its boxed layout, so the unboxed position is the offset minus the
            // object header. Scope: non-blittable struct copies in this class.
            var slot = address + (int)IL2CPP.il2cpp_field_get_offset(field) - ObjectHeaderSize;
            if (!IL2CPP.il2cpp_class_is_valuetype(fieldClass))
                IL2CPP.il2cpp_gc_wbarrier_set_field(owner, (IntPtr)slot, *(IntPtr*)slot);
            else if (fieldClass != valueClass)
                BarrierStructReferences(owner, slot, fieldClass, depth + 1);
        }
    }

    private static IntPtr ToNative(object value) => value switch
    {
        null => IntPtr.Zero,
        string text => IL2CPP.ManagedStringToIl2Cpp(text),
        Il2CppObjectBase wrapper => wrapper.Pointer,
        _ => throw new ArgumentException(
            $"{value.GetType().FullName} is not an IL2CPP object and cannot be stored in a native array."),
    };

    private static IntPtr CheckedElementClass(IntPtr array, int index, int count, out int elementSize)
    {
        if (array == IntPtr.Zero)
            throw new NullReferenceException("native array is null.");
        var length = (long)IL2CPP.il2cpp_array_length(array);
        if (index < 0 || count < 0 || index + (long)count > length)
            throw new IndexOutOfRangeException($"elements {index}..{index + count - 1} out of range for length {length}.");

        var arrayClass = IL2CPP.il2cpp_object_get_class(array);
        elementSize = IL2CPP.il2cpp_array_element_size(arrayClass);
        return IL2CPP.il2cpp_class_get_element_class(arrayClass);
    }
}
