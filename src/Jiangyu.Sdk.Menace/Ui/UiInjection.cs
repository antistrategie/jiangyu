namespace Jiangyu.Game.Ui;

/// <summary>
/// A live mod-UI injection returned by <see cref="UI"/>. Hold it to refresh the
/// element after the data behind it changes, or to take it back out. The loader
/// re-applies it automatically when the screen rebuilds, so a mod only needs
/// <see cref="Refresh"/> when its own state changed.
/// </summary>
public sealed class UiInjection
{
    private readonly RegisteredInjection _registered;

    internal UiInjection(RegisteredInjection registered) => _registered = registered;

    /// <summary>Rebuild the injected element(s) against the current tree and data.</summary>
    public void Refresh()
    {
        if (_registered == null)
            return;
        _registered.RemoveInjected();
        _registered.Reapply();
    }

    /// <summary>Remove the injected element(s) and stop maintaining this injection.</summary>
    public void Remove()
    {
        if (_registered == null)
            return;
        _registered.Dispose();
        UI.Unregister(_registered);
    }
}
