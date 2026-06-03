namespace Jiangyu.Sdk;

/// <summary>
/// Base for a code mod's entry point. The loader discovers a single
/// <see cref="JiangyuMod"/> per mod assembly, instantiates it, binds a
/// <see cref="ModContext"/>, and drives the lifecycle. Every member is a no-op by
/// default, so a mod overrides only what it needs.
/// </summary>
public abstract class JiangyuMod
{
    /// <summary>The services for this mod. Bound by the host before OnInit.</summary>
    public ModContext Context { get; internal set; } = null;

    /// <summary>Called once after the mod is loaded and its context is bound.</summary>
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

    /// <summary>Called every frame. Override only when per-frame work is needed.</summary>
    public virtual void OnUpdate() { }

    /// <summary>Called when the mod is unloaded, on shutdown or hot reload.</summary>
    public virtual void OnUnload() { }
}
