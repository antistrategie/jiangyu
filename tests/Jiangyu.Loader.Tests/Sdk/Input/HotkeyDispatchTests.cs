using Jiangyu.Loader.Sdk.Input;
using Xunit;

namespace Jiangyu.Loader.Tests.Sdk.Input;

// Exercises the hotkey dispatch core, including per-owner grouping. The KeyCode/edge to input
// mapping and the owner resolution are baked in by HotkeyRegistry, so these tests drive Add
// directly with plain closures and string owners: no UnityEngine, no live game.
public sealed class HotkeyDispatchTests
{
    private static void Noop(Exception _) { }

    [Fact]
    public void Tick_RunsHandlerWhoseSignalIsActive()
    {
        var dispatch = new HotkeyDispatch();
        var fired = 0;
        dispatch.Add(owner: null, active: () => true, when: null, handler: () => fired++);

        dispatch.Tick(Noop);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void Tick_SkipsHandlerWhoseSignalIsInactive()
    {
        var dispatch = new HotkeyDispatch();
        var fired = 0;
        dispatch.Add(owner: null, active: () => false, when: null, handler: () => fired++);

        dispatch.Tick(Noop);

        Assert.Equal(0, fired);
    }

    [Fact]
    public void Tick_GateBlocksFiringEvenWhenSignalIsActive()
    {
        var dispatch = new HotkeyDispatch();
        var fired = 0;
        dispatch.Add(owner: null, active: () => true, when: () => false, handler: () => fired++);

        dispatch.Tick(Noop);

        Assert.Equal(0, fired);
    }

    [Fact]
    public void Tick_GateCheckedEachFrame()
    {
        var dispatch = new HotkeyDispatch();
        var open = false;
        var fired = 0;
        dispatch.Add(owner: null, active: () => true, when: () => open, handler: () => fired++);

        dispatch.Tick(Noop);
        open = true;
        dispatch.Tick(Noop);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void Dispose_RemovesRegistration()
    {
        var dispatch = new HotkeyDispatch();
        var fired = 0;
        var handle = dispatch.Add(owner: null, active: () => true, when: null, handler: () => fired++);

        handle.Dispose();
        dispatch.Tick(Noop);

        Assert.Equal(0, fired);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var dispatch = new HotkeyDispatch();
        var kept = 0;
        var handle = dispatch.Add(owner: null, active: () => true, when: null, handler: () => { });
        dispatch.Add(owner: null, active: () => true, when: null, handler: () => kept++);

        handle.Dispose();
        handle.Dispose();
        dispatch.Tick(Noop);

        Assert.Equal(1, kept);
    }

    [Fact]
    public void Tick_IsolatesThrowingHandlerFromOthers()
    {
        var dispatch = new HotkeyDispatch();
        var fired = 0;
        var errors = new List<Exception>();
        dispatch.Add(owner: null, active: () => true, when: null, handler: () => throw new InvalidOperationException("boom"));
        dispatch.Add(owner: null, active: () => true, when: null, handler: () => fired++);

        dispatch.Tick(errors.Add);

        Assert.Equal(1, fired);
        Assert.Single(errors);
        Assert.IsType<InvalidOperationException>(errors[0]);
    }

    [Fact]
    public void Tick_DropsAThrowingHandlerSoItReportsOnce()
    {
        var dispatch = new HotkeyDispatch();
        var calls = 0;
        var errors = 0;
        dispatch.Add(owner: null, active: () => true, when: null, handler: () =>
        {
            calls++;
            throw new InvalidOperationException("boom");
        });

        dispatch.Tick(_ => errors++);
        dispatch.Tick(_ => errors++);
        dispatch.Tick(_ => errors++);

        Assert.Equal(1, calls);   // fired once, then dropped
        Assert.Equal(1, errors);  // reported once, never per-frame
    }

    [Fact]
    public void Tick_DropsOnlyTheThrowingHandler_OthersKeepFiring()
    {
        var dispatch = new HotkeyDispatch();
        var good = 0;
        dispatch.Add(owner: null, active: () => true, when: null, handler: () => throw new InvalidOperationException("boom"));
        dispatch.Add(owner: null, active: () => true, when: null, handler: () => good++);

        dispatch.Tick(Noop);
        dispatch.Tick(Noop);

        Assert.Equal(2, good); // the healthy handler fires every frame; the thrower is gone after frame 1
    }

    [Fact]
    public void Tick_ReportsThrowingGateAndSignal()
    {
        var dispatch = new HotkeyDispatch();
        var errors = new List<Exception>();
        dispatch.Add(owner: null, active: () => throw new InvalidOperationException("signal"), when: null, handler: () => { });
        dispatch.Add(owner: null, active: () => true, when: () => throw new InvalidOperationException("gate"), handler: () => { });

        dispatch.Tick(errors.Add);

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Dispose_DuringTick_DoesNotDisturbCurrentDispatch()
    {
        var dispatch = new HotkeyDispatch();
        var secondFired = false;
        IDisposable? second = null;
        // The first handler disposes the second mid-dispatch. Copy-on-write means this tick
        // iterates the pre-dispose snapshot, so the second still fires this frame.
        dispatch.Add(owner: null, active: () => true, when: null, handler: () => second!.Dispose());
        second = dispatch.Add(owner: null, active: () => true, when: null, handler: () => secondFired = true);

        dispatch.Tick(Noop);
        Assert.True(secondFired);

        // ...but it is gone from the next frame.
        secondFired = false;
        dispatch.Tick(Noop);
        Assert.False(secondFired);
    }

    [Fact]
    public void Add_DuringTick_DefersToNextFrame()
    {
        var dispatch = new HotkeyDispatch();
        var addedFired = 0;
        // A handler that registers another handler must not have the new one run this frame
        // (the snapshot was taken before it existed), but it runs on the next.
        dispatch.Add(owner: null, active: () => true, when: null, handler: () =>
            dispatch.Add(owner: null, active: () => true, when: null, handler: () => addedFired++));

        dispatch.Tick(Noop);
        Assert.Equal(0, addedFired);

        dispatch.Tick(Noop);
        Assert.True(addedFired >= 1);
    }

    [Fact]
    public void ClearOwner_DropsOnlyThatOwnersEntries()
    {
        var dispatch = new HotkeyDispatch();
        var a = 0;
        var b = 0;
        var untracked = 0;
        dispatch.Add(owner: "a", active: () => true, when: null, handler: () => a++);
        dispatch.Add(owner: "a", active: () => true, when: null, handler: () => a++);
        dispatch.Add(owner: "b", active: () => true, when: null, handler: () => b++);
        dispatch.Add(owner: null, active: () => true, when: null, handler: () => untracked++);

        dispatch.ClearOwner("a");
        dispatch.Tick(Noop);

        Assert.Equal(0, a);
        Assert.Equal(1, b);
        Assert.Equal(1, untracked);
    }

    [Fact]
    public void ClearOwner_UnknownOrNullOwner_IsNoOp()
    {
        var dispatch = new HotkeyDispatch();
        var fired = 0;
        dispatch.Add(owner: "a", active: () => true, when: null, handler: () => fired++);

        dispatch.ClearOwner("nope");
        dispatch.ClearOwner(null);
        dispatch.Tick(Noop);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void ClearOwner_AfterHandleDisposed_DoesNotDoubleFireOrThrow()
    {
        var dispatch = new HotkeyDispatch();
        var a = 0;
        var handle = dispatch.Add(owner: "a", active: () => true, when: null, handler: () => a++);
        dispatch.Add(owner: "a", active: () => true, when: null, handler: () => a++);

        handle.Dispose();      // the mod disposed one of its own hotkeys mid-session
        dispatch.ClearOwner("a"); // unload then drops the rest, and tolerates the gone one
        dispatch.Tick(Noop);

        Assert.Equal(0, a);
    }

    [Fact]
    public void Dispose_OfOwnedEntry_RemovesItFromTheGroup()
    {
        var dispatch = new HotkeyDispatch();
        var fired = 0;
        var handle = dispatch.Add(owner: "a", active: () => true, when: null, handler: () => fired++);

        handle.Dispose();
        dispatch.ClearOwner("a"); // nothing left to clear
        dispatch.Tick(Noop);

        Assert.Equal(0, fired);
    }
}
