using System;

namespace Jiangyu.Loader.Runtime;

// Re-applies registered mod-UI injections when the active screen changes, so an injected
// element re-lands after the game tears down and rebuilds a screen. Idle (and skipped)
// until a mod registers an injection.
internal sealed class UiInjectionDriver
{
    // After the active screen changes, re-apply each frame for this long so an injection
    // lands once the screen's content (e.g. the unit slots) finishes building, then go
    // idle. Re-apply is idempotent, so a window that outlasts the build costs nothing
    // once the injection is in place.
    private const int SettleWindowFrames = 60;

    private IntPtr _lastScreen;
    private int _settleFrames;

    public void OnSceneLoaded()
    {
        _lastScreen = IntPtr.Zero;
        _settleFrames = 0;
    }

    public void Drive()
    {
        if (!Jiangyu.Game.UI.HasInjections)
            return;

        var ptr = ActiveScreenPointer();
        if (ptr != _lastScreen)
        {
            _lastScreen = ptr;
            _settleFrames = SettleWindowFrames;
        }

        if (_settleFrames > 0)
        {
            _settleFrames--;
            Jiangyu.Game.UI.ReapplyAll();
        }
    }

    private static IntPtr ActiveScreenPointer()
    {
        try
        {
            var manager = Il2CppMenace.UI.UIManager.Get();
            var screen = manager != null ? manager.GetActiveScreen() : null;
            return screen != null ? screen.Pointer : IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }
}
