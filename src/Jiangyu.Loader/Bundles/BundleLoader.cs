using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Jiangyu.Loader.Bundles;

// TODO: Remove this wrapper once MelonLoader ships a stable release with the Unity 6
// AssetBundle fix (LavaGang/MelonLoader#1122, merged 2026-03-26 into alpha-development).
// Switch back to Il2CppAssetBundleManager at that point.
//
// Context: Unity 6 (6000.x) changed AssetBundle ICalls in three ways:
//   1. Method names gained an _Injected suffix
//   2. String/byte[] params became ManagedSpanWrapper (pointer + length)
//   3. Return values became GC handles, not raw object pointers
// The shipped Il2CppAssetBundleManager (and the game's own managed wrappers) are
// both broken in MelonLoader 0.7.2 because of this.
// See: LavaGang/MelonLoader#1057, BepInEx/Il2CppInterop#202
//
// This wrapper resolves the _Injected ICalls directly, following the same pattern
// as the fix in LavaGang/MelonLoader#1122. The bundle pointer returned by Unity 6
// is wrapped — the real pointer is at offset UnityObjectWrapperNativePointerOffset
// inside the wrapper object. For byte arrays (LoadFromMemory), raw data starts
// at offset Il2CppByteArrayDataOffset past the IL2CPP object header.
public static unsafe class BundleLoader
{
    private const int UnityObjectWrapperNativePointerOffset = 0x10;
    private const int Il2CppByteArrayDataOffset = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct ManagedSpanWrapper
    {
        public IntPtr begin;
        public int length;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr LoadFromFileDelegate(IntPtr path, uint crc, ulong offset);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetAllAssetNamesDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr LoadAssetDelegate(IntPtr self, IntPtr name, IntPtr type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr LoadAssetWithSubAssetsDelegate(IntPtr self, IntPtr name, IntPtr type);

    private static readonly LoadFromFileDelegate LoadFromFileICall =
        IL2CPP.ResolveICall<LoadFromFileDelegate>("UnityEngine.AssetBundle::LoadFromFile_Internal_Injected");

    private static readonly GetAllAssetNamesDelegate GetAllAssetNamesICall =
        IL2CPP.ResolveICall<GetAllAssetNamesDelegate>("UnityEngine.AssetBundle::GetAllAssetNames_Injected");

    private static readonly LoadAssetDelegate LoadAssetICall =
        IL2CPP.ResolveICall<LoadAssetDelegate>("UnityEngine.AssetBundle::LoadAsset_Internal_Injected");

    private static readonly LoadAssetWithSubAssetsDelegate LoadAssetWithSubAssetsICall =
        IL2CPP.ResolveICall<LoadAssetWithSubAssetsDelegate>("UnityEngine.AssetBundle::LoadAssetWithSubAssets_Internal_Injected");

    /// <summary>
    /// Resolves a GC handle returned by a Unity 6 _Injected ICall into an IL2CPP
    /// object pointer, then frees the handle.
    /// </summary>
    private static IntPtr ResolveGCHandle(IntPtr gcHandle)
    {
        if (gcHandle == IntPtr.Zero) return IntPtr.Zero;
        var objPtr = IL2CPP.il2cpp_gchandle_get_target(gcHandle);
        IL2CPP.il2cpp_gchandle_free(gcHandle);
        return objPtr;
    }

    /// <summary>
    /// Loads an AssetBundle from a file path. Returns a raw IL2CPP pointer handle.
    /// </summary>
    public static IntPtr LoadFromFile(string path)
    {
        fixed (char* chars = path)
        {
            var wrapper = new ManagedSpanWrapper { begin = (IntPtr)chars, length = path.Length };
            var gcHandle = LoadFromFileICall((IntPtr)(&wrapper), 0, 0);
            var wrappedPtr = ResolveGCHandle(gcHandle);
            if (wrappedPtr == IntPtr.Zero) return IntPtr.Zero;
            // Unity 6 wraps the bundle pointer — real pointer is at the native pointer offset.
            return Marshal.ReadIntPtr(wrappedPtr + UnityObjectWrapperNativePointerOffset);
        }
    }

    /// <summary>
    /// Gets all asset names from a loaded bundle.
    /// </summary>
    public static Il2CppStringArray GetAllAssetNames(IntPtr bundle)
    {
        var ptr = GetAllAssetNamesICall(bundle);
        if (ptr == IntPtr.Zero) return null;
        return new Il2CppStringArray(ptr);
    }

    /// <summary>
    /// Loads a named asset of the given type from a bundle. Returns a raw IL2CPP pointer.
    /// </summary>
    public static IntPtr LoadAsset(IntPtr bundle, string name, IntPtr type)
    {
        fixed (char* chars = name)
        {
            var wrapper = new ManagedSpanWrapper { begin = (IntPtr)chars, length = name.Length };
            var gcHandle = LoadAssetICall(bundle, (IntPtr)(&wrapper), type);
            // No native pointer offset for asset return values — the GC handle target IS the
            // IL2CPP managed object pointer (usable with new Mesh(ptr) etc.)
            return ResolveGCHandle(gcHandle);
        }
    }

    /// <summary>
    /// Loads a named asset and all its sub-assets (e.g. all meshes inside an FBX).
    /// Returns the IL2CPP array pointer, or IntPtr.Zero.
    /// </summary>
    public static IntPtr LoadAssetWithSubAssets(IntPtr bundle, string name, IntPtr type)
    {
        fixed (char* chars = name)
        {
            var wrapper = new ManagedSpanWrapper { begin = (IntPtr)chars, length = name.Length };
            var ptr = LoadAssetWithSubAssetsICall(bundle, (IntPtr)(&wrapper), type);
            if (ptr == IntPtr.Zero) return IntPtr.Zero;
            return ptr;
        }
    }

    /// <summary>
    /// Reads the native Unity object pointer (m_CachedPtr) from an IL2CPP managed object.
    /// For any UnityEngine.Object subclass, the native pointer is at the native pointer offset
    /// from the IL2CPP object pointer.
    /// </summary>
    public static IntPtr GetNativePtr(IntPtr il2cppObjectPtr)
    {
        if (il2cppObjectPtr == IntPtr.Zero) return IntPtr.Zero;
        return Marshal.ReadIntPtr(il2cppObjectPtr + UnityObjectWrapperNativePointerOffset);
    }

    /// <summary>
    /// Sets SkinnedMeshRenderer.sharedMesh by calling set_sharedMesh_Injected directly
    /// with native Unity pointers, bypassing the managed wrapper that may pass wrong
    /// pointer types through il2cpp_runtime_invoke.
    /// </summary>
    private static readonly IntPtr SetSharedMeshInjectedMethod;

    static BundleLoader()
    {
        var smrClass = IL2CPP.GetIl2CppClass("UnityEngine.CoreModule.dll", "UnityEngine", "SkinnedMeshRenderer");
        // Look up set_sharedMesh_Injected by name — takes 2 args (IntPtr self, IntPtr mesh)
        SetSharedMeshInjectedMethod = IL2CPP.il2cpp_class_get_method_from_name(smrClass, "set_sharedMesh_Injected", 2);
    }

    public static void SetSharedMesh(UnityEngine.SkinnedMeshRenderer smr, UnityEngine.Mesh mesh)
    {
        if (SetSharedMeshInjectedMethod == IntPtr.Zero)
            throw new Exception("Could not find set_sharedMesh_Injected method");

        var nativeSMR = GetNativePtr(IL2CPP.Il2CppObjectBaseToPtr(smr));
        var nativeMesh = GetNativePtr(IL2CPP.Il2CppObjectBaseToPtr(mesh));

        // Call: static void set_sharedMesh_Injected(IntPtr self, IntPtr mesh)
        var args = stackalloc IntPtr[2];
        args[0] = (IntPtr)(&nativeSMR);
        args[1] = (IntPtr)(&nativeMesh);

        IntPtr exception = IntPtr.Zero;
        IL2CPP.il2cpp_runtime_invoke(SetSharedMeshInjectedMethod, IntPtr.Zero, (void**)args, ref exception);

        if (exception != IntPtr.Zero)
            throw new Exception("set_sharedMesh_Injected threw an exception");
    }
}
