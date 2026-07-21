using Il2CppInterop.Runtime;

namespace Jiangyu.Loader.Net.Steam;

/// <summary>Raw reads against native Steamworks callback memory. The Il2CppInterop
/// wrapper structs are explicit-layout mirrors of the native structs, so a callback's
/// <c>pvParam</c> pointer reads directly as the wrapper type.</summary>
internal static class SteamInterop
{
    public static unsafe T Read<T>(IntPtr ptr) where T : unmanaged => *(T*)ptr;

    /// <summary>The native (boxed-value) size of a wrapper struct, for APIs that
    /// validate the caller's buffer size against the il2cpp class.</summary>
    public static int NativeSizeOf<T>() where T : unmanaged
    {
        uint align = 0;
        return IL2CPP.il2cpp_class_value_size(Il2CppClassPointerStore<T>.NativeClassPtr, ref align);
    }
}
