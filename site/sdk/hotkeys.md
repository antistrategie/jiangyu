# Hotkeys

[Hooks](/sdk/#hooks) react to the game's own moments. **Hotkeys** react to the player's keyboard: run your code when a key goes down, comes up, or is held. They are the answer to "I want a debug key" or "let the player toggle my panel" without writing a per-frame loop of your own.

The loader runs a single per-frame poll and fans each press out to every registered handler, so a mod never writes its own `Update`. You register a handler and get back an `IDisposable`. Dispose it to unregister.

The calls live in `Jiangyu.Game.Input` and are plain static calls, like `Log`. They need no `Context`:

```csharp
using Jiangyu.Game.Input;
using Jiangyu.Game.Tactical;
using UnityEngine;   // KeyCode

public sealed class DebugSystem : JiangyuSystem
{
    public override void OnInit()
    {
        Hotkeys.OnKeyDown(KeyCode.F5, () =>
            Log.Info($"active actor: {Mission.ActiveActor?.GetName()}"));
    }
}
```

The handler is your code, so call any [verb](./verbs) or game method from it: read the field, spawn a unit, toggle a flag.

## The calls

| Call | Fires |
| --- | --- |
| `OnKeyDown(key, handler, when = null)` | the frame `key` is first pressed |
| `OnKeyUp(key, handler, when = null)` | the frame `key` is released |
| `OnKeyHeld(key, handler, when = null)` | every frame `key` is held down |

`key` is UnityEngine's [`KeyCode`](https://docs.unity3d.com/ScriptReference/KeyCode.html). Each returns an `IDisposable` whose disposal removes the registration. The optional `when` is a predicate, covered next.

## Scoping a hotkey

A key you bind in `OnInit` is live for the rest of the session. Often you want it live only sometimes: while a screen is open, only in a mission, only for the player's turn. There are two idioms, and the right one depends on whether you already have a clean on and off moment.

### Gate it with `when`

Pass a `when` predicate and the handler fires only on the frames it returns `true`. The registration stays put. The poll checks the predicate fresh each frame, so it always reflects the current state:

```csharp
Hotkeys.OnKeyDown(KeyCode.G, GiveGift, when: () => GiftModal.IsOpen);
```

This is the simplest scope and the one to reach for first. It needs no lifecycle wiring, just a condition you can read.

### Tie it to a lifetime

When you already react to an open and a close (a hook, a UI callback, your own panel's show and hide), register on open and dispose on close. The hotkey exists only while the thing it belongs to does:

```csharp
public sealed class GiftPanel : JiangyuSystem
{
    private IDisposable _giftKey;

    private void OnPanelOpened()
        => _giftKey = Hotkeys.OnKeyDown(KeyCode.G, GiveGift);

    private void OnPanelClosed()
    {
        _giftKey?.Dispose();
        _giftKey = null;
    }
}
```

Both reach the same result. `when` keeps one durable registration and asks a question each frame. A lifetime keeps the registration itself in step with the feature, which suits a key whose handler captures objects you only have while the panel is up.

## Across systems

Registration is global to the loader, not owned by the system that called it. Two systems, or two mods, can bind the same key independently, and each handler runs. So you scope per feature, not per key: a [system](/sdk/#systems) for the gift panel binds `G` gated on its own modal, a debug system binds `F5` for healing, and neither knows about the other.

Registrations are cleaned up for you when your mod unloads, the same as [Coroutines](/sdk/#coroutines) and [Patches](/sdk/#patches), so you dispose a handle only to scope a hotkey *during* a session, never just to tidy up on exit.

Handlers are isolated. One that throws is reported once and then dropped, so a buggy handler cannot spam the log frame after frame, and the others keep running. Outside the game (a unit test, a tool) there is no loader hosting the poll, so a registration is a no-op and your `OnInit` does not fault.

For multi-frame or timed work that is not a keypress, reach for [Coroutines](/sdk/#coroutines). To react to the game's own moments rather than the keyboard, [Hooks](/sdk/#hooks).
