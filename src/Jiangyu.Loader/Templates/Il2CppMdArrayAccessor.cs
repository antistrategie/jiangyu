using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Direct-to-native reader/writer for multi-dimensional IL2CPP arrays.
///
/// <para><b>Why this exists.</b> Il2CppInterop's wrapper generator has no
/// template for multi-dim arrays. There is a one-line bail-out
/// <c>if (arrayType.Rank != 1) return Imports.Il2CppObjectBase</c> in
/// <c>Il2CppInterop.Generator/Contexts/AssemblyRewriteContext.cs</c> and a
/// matching one in <c>Pass80UnstripMethods.cs</c>. Properties typed
/// <c>bool[,]</c>, <c>byte[,]</c>, etc. are emitted as
/// <c>Il2CppObjectBase</c>, and the generated getter throws
/// <c>NullReferenceException</c> at runtime because the wrapper has no
/// constructor path for multi-dim arrays. The IL2CPP-side memory IS
/// populated correctly (Sirenix Odin's <c>OnAfterDeserialize</c> writes
/// to the field via the IL2CPP runtime); only the C# wrapper layer is
/// broken. Tracked as
/// <see href="https://github.com/BepInEx/Il2CppInterop/issues/218"/>:
/// open since 2024-09, milestone 2.0.0, planned but not in flight.
/// </para>
///
/// <para><b>Layout.</b> IL2CPP arrays follow a stable layout that's been
/// unchanged since Unity 2019.3. Documented in Unity's
/// <c>il2cpp-object-internals.h</c>; verified against multiple
/// modding-tool reference dumps. 64-bit:
/// <code>
/// offset  0: klass*       (8)
/// offset  8: monitor*     (8)
/// offset 16: bounds*      (8): null for SZ arrays, non-null for rank &gt;= 2
/// offset 24: max_length   (8): total element count (product of all dims)
/// offset 32: element data: sizeof(T) * max_length, row-major
/// </code>
/// <c>bounds*</c> points to a separate buffer of <c>rank</c> entries:
/// <code>
/// offset  0: length       (8)
/// offset  8: lower_bound  (4) + 4 bytes padding
/// </code>
/// </para>
///
/// <para><b>Prior art.</b> The
/// <see href="https://github.com/rinnyanneko/SimRailConnect/blob/main/src/SimRailConnect/GameBridge.cs">
/// SimRailConnect</see> mod is the only production reference I found that
/// hits this exact wall (a generic-base-class <c>T[,]</c> field). The
/// pattern below: class lookup, field-name walk through the inheritance
/// chain, plausibility-check the array pointer, then walk row-major
/// elements at offset 0x20. Comes straight from there.
/// </para>
/// </summary>
internal static class Il2CppMdArrayAccessor
{
    // Layout constants (64-bit IL2CPP). Stable since Unity 2019.3.
    private const int Il2CppArrayBoundsOffset = 0x10;
    private const int Il2CppArrayMaxLengthOffset = 0x18;
    private const int Il2CppArrayDataOffset = 0x20;
    private const int Il2CppArrayBoundsRecordSize = 16; // 8 length + 4 lb + 4 pad

    // Plausibility floor for a heap pointer. Below this is either null,
    // unmapped, or kernel-reserved low memory; mirrors the SimRailConnect
    // sanity-check (>0x10000, 8-byte aligned).
    private const long MinPlausiblePointer = 0x10000;

    /// <summary>
    /// Resolves the IL2CPP-side array pointer behind
    /// <paramref name="instance"/>'s <paramref name="fieldName"/> field.
    /// Walks the type hierarchy looking for the field by name (matches
    /// the pattern <see cref="TemplateCloneApplier"/> already uses for
    /// the m_ID write). Returns the bare native IntPtr :bypassing the
    /// broken wrapper getter entirely.
    /// </summary>
    public static bool TryGetFieldArrayPointer(
        object instance, string fieldName, out IntPtr arrayPtr, out string error)
    {
        arrayPtr = IntPtr.Zero;
        error = null;

        if (instance is not Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase il2cppObj)
        {
            error = "instance is not an Il2CppObjectBase wrapper.";
            return false;
        }

        var instPtr = il2cppObj.Pointer;
        if (instPtr == IntPtr.Zero)
        {
            error = "instance native pointer is null.";
            return false;
        }

        var klass = IL2CPP.il2cpp_object_get_class(instPtr);
        if (klass == IntPtr.Zero)
        {
            error = "il2cpp_object_get_class returned null.";
            return false;
        }

        var fieldPtr = FindFieldInHierarchy(klass, fieldName);
        if (fieldPtr == IntPtr.Zero)
        {
            error = $"field '{fieldName}' not found in IL2CPP class hierarchy.";
            return false;
        }

        var fieldOffset = IL2CPP.il2cpp_field_get_offset(fieldPtr);
        if (fieldOffset == 0)
        {
            error = $"field '{fieldName}' has zero offset (suspicious).";
            return false;
        }

        var fieldAddr = instPtr + (int)fieldOffset;
        var rawPtr = Marshal.ReadIntPtr(fieldAddr);

        if (rawPtr == IntPtr.Zero)
        {
            error = $"field '{fieldName}' is null on this instance.";
            return false;
        }

        // Plausibility check: array heap pointers are well above null
        // and 8-byte aligned. SimRailConnect uses the same heuristic to
        // catch the case where a non-array field was probed by mistake.
        if ((long)rawPtr < MinPlausiblePointer || ((long)rawPtr & 7) != 0)
        {
            error = $"field '{fieldName}' pointer 0x{(long)rawPtr:X} is not a plausible array.";
            return false;
        }

        arrayPtr = rawPtr;
        return true;
    }

    /// <summary>
    /// Reads the <c>bounds[]</c> table of an IL2CPP multi-dim array and
    /// returns the per-axis lengths. Validates that the product of the
    /// lengths equals the array's <c>max_length</c> :a cheap sanity
    /// check that catches misidentified pointers without crashing the
    /// game on a stray heap read.
    /// </summary>
    public static bool TryReadDimensions(
        IntPtr arrayPtr, int expectedRank, out int[] dimensions, out string error)
    {
        dimensions = null;
        error = null;

        var boundsPtr = Marshal.ReadIntPtr(arrayPtr + Il2CppArrayBoundsOffset);
        if (boundsPtr == IntPtr.Zero)
        {
            error = "array bounds pointer is null (this is an SZ array, not multi-dim).";
            return false;
        }
        if ((long)boundsPtr < MinPlausiblePointer || ((long)boundsPtr & 7) != 0)
        {
            error = $"bounds pointer 0x{(long)boundsPtr:X} is not plausible.";
            return false;
        }

        var dims = new int[expectedRank];
        long product = 1;
        for (var i = 0; i < expectedRank; i++)
        {
            // bounds[i].length is a uintptr_t (8 bytes on 64-bit). Read
            // as Int64 since IL2CPP only ever uses values that fit.
            var length = Marshal.ReadInt64(boundsPtr, i * Il2CppArrayBoundsRecordSize);
            if (length < 0 || length > int.MaxValue)
            {
                error = $"bounds[{i}].length {length} is implausible.";
                return false;
            }
            dims[i] = (int)length;
            product *= length;
        }

        var maxLength = Marshal.ReadInt64(arrayPtr + Il2CppArrayMaxLengthOffset);
        if (product != maxLength)
        {
            error = $"product of dims ({product}) != array max_length ({maxLength}); "
                + "the rank guess is probably wrong or this isn't an MD array.";
            return false;
        }

        dimensions = dims;
        return true;
    }

    /// <summary>Reads a 1-byte cell at the given row-major index.</summary>
    public static byte ReadByteCell(IntPtr arrayPtr, int rowMajorIndex)
        => Marshal.ReadByte(arrayPtr + Il2CppArrayDataOffset + rowMajorIndex);

    /// <summary>Writes a 1-byte cell at the given row-major index.</summary>
    public static void WriteByteCell(IntPtr arrayPtr, int rowMajorIndex, byte value)
        => Marshal.WriteByte(arrayPtr + Il2CppArrayDataOffset + rowMajorIndex, value);

    /// <summary>
    /// Reads a 4-byte (Int32) cell at the given row-major index.
    /// Element offset multiplies the row-major index by sizeof(int).
    /// </summary>
    public static int ReadInt32Cell(IntPtr arrayPtr, int rowMajorIndex)
        => Marshal.ReadInt32(arrayPtr + Il2CppArrayDataOffset + rowMajorIndex * sizeof(int));

    /// <summary>Writes a 4-byte (Int32) cell at the given row-major index.</summary>
    public static void WriteInt32Cell(IntPtr arrayPtr, int rowMajorIndex, int value)
        => Marshal.WriteInt32(arrayPtr + Il2CppArrayDataOffset + rowMajorIndex * sizeof(int), value);

    /// <summary>
    /// Computes the row-major flat index for an MD address against the
    /// declared dimensions. Returns false on out-of-range coords.
    /// </summary>
    public static bool TryComputeRowMajorIndex(
        IReadOnlyList<int> coords, IReadOnlyList<int> dimensions,
        out int flatIndex, out string error)
    {
        flatIndex = 0;
        error = null;

        if (coords.Count != dimensions.Count)
        {
            error = $"coord rank {coords.Count} != array rank {dimensions.Count}.";
            return false;
        }

        // Standard row-major: idx = sum_{i} coord[i] * stride[i],
        // stride[i] = product of dims[i+1..end].
        long flat = 0;
        long stride = 1;
        for (var i = coords.Count - 1; i >= 0; i--)
        {
            var c = coords[i];
            if (c < 0 || c >= dimensions[i])
            {
                error = $"coord[{i}]={c} out of range 0..{dimensions[i] - 1}.";
                return false;
            }
            flat += c * stride;
            stride *= dimensions[i];
        }

        if (flat > int.MaxValue)
        {
            error = $"flat index {flat} exceeds Int32.MaxValue.";
            return false;
        }
        flatIndex = (int)flat;
        return true;
    }

    private static IntPtr FindFieldInHierarchy(IntPtr klass, string fieldName)
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
