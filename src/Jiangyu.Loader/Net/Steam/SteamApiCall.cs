using System.Runtime.InteropServices;
using Il2CppSteamworks;

namespace Jiangyu.Loader.Net.Steam;

/// <summary>
/// A pending Steam API call polled to completion through <see cref="SteamUtils"/>.
/// Stands in for Steamworks.NET's <c>CallResult&lt;T&gt;</c>, whose generic il2cpp
/// instances only exist for the callback types the game itself awaits.
/// </summary>
internal sealed class SteamApiCall<T> where T : unmanaged
{
    private readonly SteamAPICall_t _handle;

    public SteamApiCall(SteamAPICall_t handle) => _handle = handle;

    // The wrapper struct's static k_iCallback property carries the native callback id;
    // reflection is the only route to a static on a generic parameter.
    private static readonly int CallbackId =
        (int)typeof(T).GetProperty("k_iCallback").GetValue(null);

    /// <summary>Poll once. True when the call finished: <paramref name="failure"/>
    /// carries the failure detail, otherwise <paramref name="result"/> holds the
    /// callback struct.</summary>
    public bool TryComplete(out T result, out string failure)
    {
        result = default;
        failure = null;
        if (_handle.m_SteamAPICall == 0)
        {
            failure = "steam refused the call (invalid api call handle)";
            return true;
        }

        if (!SteamUtils.IsAPICallCompleted(_handle, out var ioFailed))
            return false;
        if (ioFailed)
        {
            failure = $"api call failed ({SteamUtils.GetAPICallFailureReason(_handle)})";
            return true;
        }

        var size = SteamInterop.NativeSizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!SteamUtils.GetAPICallResult(_handle, buffer, size, CallbackId, out var resultFailed) || resultFailed)
            {
                failure = $"result read failed (callback id {CallbackId}, size {size}, "
                    + $"failed={resultFailed}, reason {SteamUtils.GetAPICallFailureReason(_handle)})";
                return true;
            }

            result = SteamInterop.Read<T>(buffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
