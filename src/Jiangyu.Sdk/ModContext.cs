namespace Jiangyu.Sdk;

/// <summary>Per-mod logging, tagged with the mod id by the host. <see cref="Debug"/>
/// is dropped unless the dev opts in (shares <see cref="Log.MinLevel"/>).</summary>
public interface IModLog
{
    void Debug(string message);

    void Info(string message);

    void Warn(string message);

    void Error(string message);
}

/// <summary>
/// Per-save-slot, mod-owned state for genuinely out-of-band data the game save
/// does not already persist. Most mod state should be encoded as game state
/// (a marker skill, stacks, a duration buff) and needs none of this.
///
/// Mutate the live blobs from <see cref="Get{T}"/>; the loader serialises them to a
/// sidecar beside the save file when the game saves and reloads them when it loads,
/// keyed by the save slot so state never leaks across slots.
/// </summary>
public interface IModState
{
    /// <summary>The live, persisted blob of type <typeparamref name="T"/> (a plain
    /// serialisable class the mod owns). Mutate it in place; it is saved with the game.</summary>
    T Get<T>() where T : class, new();

    /// <summary>Not required: state persists automatically when the game saves. Kept
    /// for API stability; a no-op.</summary>
    void Save();
}

/// <summary>
/// Global, no-anchor moments (every kill, a round boundary, save or load).
/// Subscriptions are scoped to the mod's lifetime.
/// </summary>
public interface IHookBus
{
    IDisposable Subscribe<T>(Action<T> handler) where T : class;
}

/// <summary>What a patched method call exposes to a mod's patch handler.</summary>
public sealed class PatchInfo
{
    private object _result;

    public PatchInfo(object instance, IReadOnlyList<object> args)
    {
        Instance = instance;
        Args = args ?? Array.Empty<object>();
    }

    public PatchInfo(object instance, IReadOnlyList<object> args, object result)
        : this(instance, args)
    {
        _result = result;
    }

    /// <summary>The receiver the method was called on, or null for a static method.
    /// Cast it to the game type the mod references.</summary>
    public object Instance { get; }

    /// <summary>The call's arguments, boxed. Cast each to the game type as needed.</summary>
    public IReadOnlyList<object> Args { get; }

    /// <summary>Set from a prefix handler to stop the original method running. Ignored
    /// for a postfix (the original has already run).</summary>
    public bool Skip { get; set; }

    /// <summary>The original method's return value, boxed, as a postfix handler sees it.
    /// Assigning overrides the value the caller receives. Overriding is honoured for
    /// targets returning <c>int</c>, <c>bool</c> or <c>float</c> (only when the value
    /// assigned is of that exact type), and for reference-typed returns (only when the
    /// value assigned is null or an instance of the return type). A mismatched type is
    /// ignored, never coerced; other value-typed returns ignore the assignment. Always
    /// null in a prefix handler.</summary>
    public object Result
    {
        get => _result;
        set
        {
            _result = value;
            IsResultOverridden = true;
        }
    }

    /// <summary>Whether a handler assigned <see cref="Result"/> during this dispatch.</summary>
    public bool IsResultOverridden { get; private set; }
}

/// <summary>
/// Patches a game method the hook bus does not cover. The mod names the method by
/// its declaring type and method name; the handler runs before (prefix) or after
/// (postfix) it. Patches are tracked per mod and removed when the mod unloads. This
/// is the escape hatch: prefer a <see cref="IHookBus"/> hook where one exists.
/// </summary>
public interface IModPatches
{
    /// <summary>Run <paramref name="handler"/> before <c>typeName.methodName</c>. The
    /// handler may set <see cref="PatchInfo.Skip"/> to stop the original running.
    /// If the method name is overloaded, no patch is registered and a warning is
    /// logged: use the <see cref="Prefix(string,string,int,Action{PatchInfo})"/>
    /// overload to disambiguate by parameter count.</summary>
    void Prefix(string typeName, string methodName, Action<PatchInfo> handler);

    /// <summary>Run <paramref name="handler"/> after <c>typeName.methodName</c> returns.
    /// If the method name is overloaded, no patch is registered and a warning is
    /// logged: use the <see cref="Postfix(string,string,int,Action{PatchInfo})"/>
    /// overload to disambiguate by parameter count.</summary>
    void Postfix(string typeName, string methodName, Action<PatchInfo> handler);

    /// <summary>As <see cref="Prefix(string,string,Action{PatchInfo})"/>, selecting the
    /// overload of <c>typeName.methodName</c> that takes exactly
    /// <paramref name="parameterCount"/> parameters. Use this when the method is
    /// overloaded (e.g. a method with 3- and 4-argument forms).</summary>
    void Prefix(string typeName, string methodName, int parameterCount, Action<PatchInfo> handler);

    /// <summary>As <see cref="Postfix(string,string,Action{PatchInfo})"/>, selecting the
    /// overload of <c>typeName.methodName</c> that takes exactly
    /// <paramref name="parameterCount"/> parameters.</summary>
    void Postfix(string typeName, string methodName, int parameterCount, Action<PatchInfo> handler);
}

/// <summary>
/// Runs a mod's multi-frame or timed logic as a coroutine. Use it to act a beat
/// after a synchronous hook (once the game state has settled), to poll for a
/// condition that has no hook, or to sequence an effect over time. Routines are
/// tracked per mod and stopped when the mod unloads.
/// </summary>
public interface IModCoroutines
{
    /// <summary>
    /// Starts <paramref name="routine"/> and returns a handle to pass to
    /// <see cref="Stop"/>. The routine is a normal C# iterator: <c>yield return null</c>
    /// resumes next frame, and the mod may yield UnityEngine wait instructions
    /// (<c>WaitForSeconds</c>, <c>WaitForEndOfFrame</c>) since the mod references
    /// UnityEngine. An exception thrown by the routine is logged against the mod and
    /// stops only that routine.
    /// </summary>
    object Start(System.Collections.IEnumerator routine);

    /// <summary>Stops a routine started by <see cref="Start"/>. A null or already
    /// finished handle is ignored.</summary>
    void Stop(object handle);
}

/// <summary>
/// The mod's own bundled assets, loaded on demand by name. Scoped to this mod:
/// only assets from the mod's <c>.bundle</c> files are visible, never another
/// mod's. The loaded asset is owned by the loader and kept alive for the session.
/// </summary>
public interface IModAssets
{
    /// <summary>
    /// The asset of type <typeparamref name="T"/> named <paramref name="name"/> from
    /// the mod's bundles, or null if no such asset exists. <typeparamref name="T"/> is
    /// a UnityEngine type (<c>GameObject</c>, <c>Sprite</c>, <c>AudioClip</c>, a
    /// <c>ScriptableObject</c> subtype, and so on). <paramref name="name"/> matches the
    /// asset's short name (no extension) or its full path inside the bundle.
    /// </summary>
    T Load<T>(string name) where T : class;

    /// <summary>Loads the asset into <paramref name="asset"/> and returns true, or
    /// returns false and null when it is absent.</summary>
    bool TryLoad<T>(string name, out T asset) where T : class;

    /// <summary>The full asset paths across all of the mod's bundles.</summary>
    IReadOnlyList<string> Names { get; }
}

/// <summary>
/// The services a mod receives from the loader. The host supplies the concrete
/// implementation and binds it to the mod's systems before <see cref="JiangyuSystem.OnInit"/>.
/// </summary>
public abstract class ModContext
{
    private static Func<object, ModContext> _resolver = static _ => null;

    /// <summary>Bind the host's instance-to-context resolver. Called by the loader at startup.</summary>
    public static void BindResolver(Func<object, ModContext> resolver)
        => _resolver = resolver ?? (static _ => null);

    /// <summary>
    /// The context owning <paramref name="instance"/>, resolved by the mod assembly
    /// that defines its type, or null for an object outside any loaded mod. Code with
    /// no <see cref="ModContext"/> of its own — an injected <c>[JiangyuType]</c> handler
    /// the game constructs — calls <c>ModContext.For(this)</c> to reach the mod's
    /// <see cref="Log"/>, <see cref="State"/>, and <see cref="ModFolder"/>.
    /// </summary>
    public static ModContext For(object instance) => _resolver(instance);

    /// <summary>The mod's id, used for type namespacing and logging.</summary>
    public abstract string ModId { get; }

    /// <summary>
    /// Absolute path to this mod's deployed folder (<c>Mods/&lt;ModId&gt;</c>).
    /// Read the mod's own bundled files relative to here, and write any
    /// out-of-band files the mod owns.
    /// </summary>
    public abstract string ModFolder { get; }

    /// <summary>The mod's version, read from its manifest (<c>jiangyu.json</c>).</summary>
    public abstract string Version { get; }

    public abstract IModLog Log { get; }

    public abstract IModState State { get; }

    public abstract IHookBus Hooks { get; }

    /// <summary>The mod's own bundled assets, loaded on demand by name.</summary>
    public abstract IModAssets Assets { get; }

    /// <summary>Runs the mod's multi-frame or timed logic as coroutines.</summary>
    public abstract IModCoroutines Coroutines { get; }

    /// <summary>Patches game methods the hook bus does not cover. The escape hatch.</summary>
    public abstract IModPatches Patches { get; }
}
