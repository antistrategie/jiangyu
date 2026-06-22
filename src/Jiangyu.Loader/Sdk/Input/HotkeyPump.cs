using System;
using System.Collections;

namespace Jiangyu.Loader.Sdk.Input;

// One per-frame coroutine drives the whole hotkey dispatch. Started once at init and hosted
// on MelonLoader's persistent coroutine object, so it survives scene loads.
internal static class HotkeyPump
{
    public static IEnumerator Poll(HotkeyDispatch dispatch, Action<Exception> onError)
    {
        while (true)
        {
            dispatch.Tick(onError);
            yield return null; // resume next frame
        }
    }
}
