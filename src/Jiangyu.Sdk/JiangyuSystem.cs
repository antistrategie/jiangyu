namespace Jiangyu.Sdk;

/// <summary>
/// Base for a unit of mod behaviour. A mod ships as many systems as it likes, one per
/// feature: the loader discovers every <see cref="JiangyuSystem"/> in the mod's
/// assembly, instantiates each, binds the mod's shared <see cref="ModContext"/>, and
/// drives the lifecycle. Every member is a no-op by default, so a system overrides only
/// what it needs.
///
/// <para>The systems of one mod share a single <see cref="ModContext"/> (the same
/// <see cref="ModContext.State"/>, hooks, patches, and assets), so they cooperate
/// through it without wiring each other up. They run in a stable, name-ordered
/// sequence. When one system must initialise after another, declare it with
/// <see cref="DependsOnAttribute"/> and the loader runs that dependency first on init
/// and tears systems down in reverse on unload.</para>
/// </summary>
public abstract class JiangyuSystem
{
    /// <summary>The mod's services, shared by every system of the mod. Bound by the host before OnInit.</summary>
    public ModContext Context { get; internal set; } = null;

    /// <summary>Called once after the system is loaded and its context is bound.</summary>
    public virtual void OnInit() { }

    /// <summary>
    /// Called once the loader has applied every mod's template clones and patches to
    /// the live game templates. Read or further adjust the final merged template set
    /// here: unlike <see cref="OnSceneLoaded"/>, it is guaranteed to run after the
    /// edits have landed.
    /// </summary>
    public virtual void OnTemplatesApplied() { }

    /// <summary>Called when a Unity scene finishes loading.</summary>
    public virtual void OnSceneLoaded(int buildIndex, string sceneName) { }

    /// <summary>Called when the system is unloaded, on shutdown or hot reload.</summary>
    public virtual void OnUnload() { }
}
