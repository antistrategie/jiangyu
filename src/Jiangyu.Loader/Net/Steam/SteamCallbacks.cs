using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppSteamworks;

namespace Jiangyu.Loader.Net.Steam;

/// <summary>
/// A Steamworks.NET <see cref="Callback"/> implemented managed-side and injected into
/// IL2CPP. The game's own SteamManager pumps <c>SteamAPI.RunCallbacks</c> every frame
/// and <see cref="CallbackDispatcher"/> dispatches to registered callbacks by the
/// identity of <see cref="GetCallbackType"/>, so this one injected class serves every
/// callback struct: each instance carries its target type and hands the raw struct
/// pointer to a managed handler.
/// </summary>
internal sealed class SteamCallbackSink : Callback
{
    // Assigned directly rather than through a setter: Il2CppInterop scans an injected
    // type's methods for il2cpp exposure and warns on managed-only signatures.
    internal Il2CppSystem.Type CallbackType;
    internal Action<IntPtr> OnRun;

    public SteamCallbackSink(IntPtr pointer)
        : base(pointer)
    {
    }

    public SteamCallbackSink()
        : base(ClassInjector.DerivedConstructorPointer<SteamCallbackSink>())
        => ClassInjector.DerivedConstructorBody(this);

    public override bool IsGameServer => false;

    public override Il2CppSystem.Type GetCallbackType() => CallbackType;

    public override void OnRunCallback(IntPtr pvParam)
    {
        // A throwing handler must not unwind into the game's callback pump.
        try
        {
            OnRun?.Invoke(pvParam);
        }
        catch (Exception ex)
        {
            SteamCallbacks.LogError($"callback handler failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public override void SetUnregistered()
    {
    }
}

/// <summary>
/// Registers managed listeners for Steam callbacks on the game's own dispatcher.
/// Registration goes through <see cref="CallbackDispatcher.Register(Callback)"/>, whose
/// native identity lookup reads the callback struct's <c>CallbackIdentityAttribute</c>;
/// if that lookup fails at runtime the sink is inserted into the dispatcher's registry
/// directly under the struct's <c>k_iCallback</c> id.
/// </summary>
internal static class SteamCallbacks
{
    private static readonly List<SteamCallbackSink> Alive = new();
    private static bool _typeInjected;

    internal static Action<string> Log = _ => { };

    public static void Listen<T>(Action<IntPtr> onRun) where T : unmanaged
    {
        if (!_typeInjected)
        {
            ClassInjector.RegisterTypeInIl2Cpp<SteamCallbackSink>();
            _typeInjected = true;
        }

        var sink = new SteamCallbackSink
        {
            CallbackType = Il2CppType.Of<T>(),
            OnRun = onRun,
        };
        try
        {
            CallbackDispatcher.Register(sink);
        }
        catch (Exception ex)
        {
            Log($"dispatcher Register failed for {typeof(T).Name} ({ex.GetType().Name}: {ex.Message}); inserting directly");
            RegisterDirect(sink, ReadCallbackId<T>());
        }

        Alive.Add(sink);
    }

    /// <summary>Unregister every live sink. Best-effort: a sink that was inserted
    /// directly is removed from the registry list the same way it went in.</summary>
    public static void Clear()
    {
        foreach (var sink in Alive)
        {
            try
            {
                CallbackDispatcher.Unregister(sink);
            }
            catch (Exception ex)
            {
                Log($"dispatcher Unregister failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Alive.Clear();
    }

    internal static void LogError(string message) => Log(message);

    private static void RegisterDirect(SteamCallbackSink sink, int callbackId)
    {
        var registry = CallbackDispatcher.m_registeredCallbacks
            ?? throw new InvalidOperationException("CallbackDispatcher is not initialised (Steam API not up?)");
        if (!registry.TryGetValue(callbackId, out var list))
        {
            list = new Il2CppSystem.Collections.Generic.List<Callback>();
            registry[callbackId] = list;
        }

        list.Add(sink);
    }

    // Each wrapper callback struct exposes its native id as a static k_iCallback
    // property; there is no non-reflective way to reach a static on a generic parameter.
    private static int ReadCallbackId<T>() where T : unmanaged
    {
        var property = typeof(T).GetProperty("k_iCallback")
            ?? throw new InvalidOperationException($"{typeof(T).Name} has no k_iCallback");
        return (int)property.GetValue(null);
    }
}
