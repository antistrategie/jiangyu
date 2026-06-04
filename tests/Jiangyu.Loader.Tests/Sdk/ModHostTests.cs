using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Jiangyu.Loader.Logging;
using Jiangyu.Loader.Sdk;
using Jiangyu.Loader.Sdk.Hooks;
using Jiangyu.Loader.Sdk.Patches;
using Jiangyu.Loader.Sdk.State;
using Jiangyu.Sdk;
using Xunit;

namespace Jiangyu.Loader.Tests.Sdk;

public class ModHostTests
{
    private sealed class CollectingLog : IModHostLog
    {
        public readonly List<string> Debugs = new();
        public readonly List<string> Infos = new();
        public readonly List<string> Warns = new();
        public readonly List<string> Errors = new();

        public void Debug(string message) => Debugs.Add(message);
        public void Info(string message) => Infos.Add(message);
        public void Warn(string message) => Warns.Add(message);
        public void Error(string message) => Errors.Add(message);
    }

    public sealed class TestBlob
    {
        public int Count { get; set; }
        public string? Note { get; set; }
    }

    [Fact]
    public void PersistentModState_round_trips_through_serialize_and_load()
    {
        var a = new PersistentModState();
        var blob = a.Get<TestBlob>();
        blob.Count = 42;
        blob.Note = "hello";

        var b = new PersistentModState();
        b.Load(a.Serialize());

        Assert.Equal(42, b.Get<TestBlob>().Count);
        Assert.Equal("hello", b.Get<TestBlob>().Note);
    }

    [Fact]
    public void PersistentModState_keeps_unread_blobs_across_a_resave()
    {
        var seed = new PersistentModState();
        seed.Get<TestBlob>().Count = 7;

        var middle = new PersistentModState();
        middle.Load(seed.Serialize());   // loaded but never read
        var resaved = middle.Serialize(); // re-saved without realising the blob

        var final = new PersistentModState();
        final.Load(resaved);
        Assert.Equal(7, final.Get<TestBlob>().Count);
    }

    [Fact]
    public void ModStateStore_persists_state_per_save_path_and_reloads_it()
    {
        var savePath = Path.Combine(Path.GetTempPath(), $"jiangyu-state-{Guid.NewGuid():N}.save");
        var sidecar = savePath + ".jiangyu.mymod.json";
        try
        {
            var host = NewHost(out var log);
            host.Adopt("mymod", new GoodMod());
            ((PersistentModState)host.ContextFor("mymod").State).Get<TestBlob>().Count = 99;

            new ModStateStore(host, log).WriteAll(savePath);
            Assert.True(File.Exists(sidecar));

            // A fresh session: a new host loads the same save path.
            var reloaded = NewHost(out var log2);
            reloaded.Adopt("mymod", new GoodMod());
            new ModStateStore(reloaded, log2).LoadAll(savePath);

            Assert.Equal(99, ((PersistentModState)reloaded.ContextFor("mymod").State).Get<TestBlob>().Count);
        }
        finally
        {
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }
    }

    private sealed class GoodMod : JiangyuMod
    {
        public int Inits;
        public int Updates;
        public int Scenes;
        public int Unloads;
        public int TemplatesApplied;

        public override void OnInit() => Inits++;
        public override void OnTemplatesApplied() => TemplatesApplied++;
        public override void OnUpdate() => Updates++;
        public override void OnSceneLoaded(int buildIndex, string sceneName) => Scenes++;
        public override void OnUnload() => Unloads++;
    }

    private sealed class ThrowingMod : JiangyuMod
    {
        public int Attempts;

        public override void OnUpdate()
        {
            Attempts++;
            throw new InvalidOperationException("boom");
        }
    }

    private static readonly string ModsDir = Path.Combine(Path.GetTempPath(), "jiangyu-mods");

    private static ModHost NewHost(out CollectingLog log)
    {
        log = new CollectingLog();
        return new ModHost(log, LoaderModContext.Factory(log, new InProcessHookBus(log), ModsDir));
    }

    // A host whose contexts use a real coroutine runner: coroutineStart returns a handle
    // (without advancing the routine, so it stays "running") and coroutineStop records
    // which handles were stopped, so a teardown is observable.
    private static ModHost NewHostWithCoroutineSpy(out CollectingLog log, List<object> stopped)
    {
        var hostLog = new CollectingLog();
        log = hostLog;
        var factory = LoaderModContext.Factory(
            hostLog, new InProcessHookBus(hostLog), ModsDir,
            coroutineStart: _ => new object(),
            coroutineStop: handle => stopped.Add(handle));
        return new ModHost(hostLog, factory);
    }

    [Fact]
    public void Quarantining_one_entry_point_keeps_a_sibling_entry_points_coroutines_running()
    {
        var stopped = new List<object>();
        var host = NewHostWithCoroutineSpy(out _, stopped);

        // Two entry points share modId "dual" -> one context, one coroutine runner.
        host.Adopt("dual", new ThrowingMod());
        host.Adopt("dual", new GoodMod());
        host.ContextFor("dual").Coroutines.Start(Forever());

        for (var i = 0; i < 5; i++)
            host.Update(); // the ThrowingMod entry point quarantines after 3 throws

        // The good sibling is still live, so the shared coroutine is not torn down.
        Assert.Empty(stopped);

        static IEnumerator Forever() { yield return null; }
    }

    [Fact]
    public void Quarantining_the_only_entry_point_stops_its_coroutines()
    {
        var stopped = new List<object>();
        var host = NewHostWithCoroutineSpy(out _, stopped);

        host.Adopt("solo", new ThrowingMod());
        host.ContextFor("solo").Coroutines.Start(Forever());

        for (var i = 0; i < 5; i++)
            host.Update();

        // No sibling entry point -> quarantine tears down the mod's coroutines.
        Assert.NotEmpty(stopped);

        static IEnumerator Forever() { yield return null; }
    }

    [Fact]
    public void Adopt_binds_context_and_forwards_lifecycle()
    {
        var host = NewHost(out _);
        var mod = new GoodMod();
        host.Adopt("mymod", mod);

        Assert.NotNull(mod.Context);
        Assert.Equal("mymod", mod.Context.ModId);
        Assert.Equal(Path.Combine(ModsDir, "mymod"), mod.Context.ModFolder);
        // No manifest at that path, so the version falls back to the unknown sentinel.
        Assert.Equal("0.0.0", mod.Context.Version);

        host.InitAll();
        host.TemplatesApplied();
        host.SceneLoaded(1, "Tactical");
        host.Update();
        host.UnloadAll();

        Assert.Equal(1, mod.Inits);
        Assert.Equal(1, mod.TemplatesApplied);
        Assert.Equal(1, mod.Scenes);
        Assert.Equal(1, mod.Updates);
        Assert.Equal(1, mod.Unloads);
    }

    [Fact]
    public void One_mod_throwing_does_not_stop_others()
    {
        var host = NewHost(out var log);
        var good = new GoodMod();
        var bad = new ThrowingMod();
        host.Adopt("good", good);
        host.Adopt("bad", bad);

        host.Update();

        Assert.Equal(1, good.Updates);
        Assert.Equal(1, bad.Attempts);
        Assert.Contains(log.Errors, e => e.Contains("bad") && e.Contains("OnUpdate"));
    }

    [Fact]
    public void Repeated_failures_quarantine_the_mod()
    {
        var host = NewHost(out var log);
        var bad = new ThrowingMod();
        var loaded = host.Adopt("bad", bad);

        for (var i = 0; i < 5; i++)
            host.Update();

        Assert.True(loaded.Quarantined);
        Assert.Equal(3, bad.Attempts);
        Assert.Contains(log.Warns, w => w.Contains("quarantined"));
    }

    [Fact]
    public void Context_reads_version_from_deployed_manifest()
    {
        const string modId = "vermod";
        var folder = Path.Combine(ModsDir, modId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "jiangyu.json"), "{ \"name\": \"vermod\", \"version\": \"1.2.3\" }");
        try
        {
            var host = NewHost(out _);
            var mod = new GoodMod();
            host.Adopt(modId, mod);

            Assert.Equal("1.2.3", mod.Context.Version);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Register_discovers_concrete_jiangyumod_entries()
    {
        var host = NewHost(out _);
        var registered = host.Register(typeof(GoodMod).Assembly, "mymod");

        Assert.Contains(registered, m => m.Instance is GoodMod);
    }

    [Fact]
    public void ContextFor_is_stable_per_mod_id()
    {
        var host = NewHost(out _);

        Assert.Same(host.ContextFor("a"), host.ContextFor("a"));
        Assert.NotSame(host.ContextFor("a"), host.ContextFor("b"));
    }

    [Fact]
    public void ResolveContext_maps_an_instance_to_its_mod_assembly_context()
    {
        var host = NewHost(out _);
        host.Register(typeof(GoodMod).Assembly, "mymod");

        Assert.Same(host.ContextFor("mymod"), host.ResolveContext(new GoodMod()));
        // A string is defined in the runtime, not a registered mod assembly.
        Assert.Null(host.ResolveContext("outside any mod"));
    }

    [Fact]
    public void Static_log_is_auto_tagged_with_the_caller_mod()
    {
        var host = NewHost(out _);
        host.Register(typeof(ModHostTests).Assembly, "testmod");

        var captured = new List<string>();
        Jiangyu.Sdk.Log.Bind((_, msg) => captured.Add(msg));
        Jiangyu.Sdk.Log.BindModResolver(host.ModIdForAssembly);
        try
        {
            Jiangyu.Sdk.Log.Info("hello");
            Assert.Contains("[testmod] hello", captured);
        }
        finally
        {
            Jiangyu.Sdk.Log.Bind(null);
            Jiangyu.Sdk.Log.BindModResolver(null);
        }
    }

    [Fact]
    public void Hooks_subscribe_receives_published_context()
    {
        var log = new CollectingLog();
        var bus = new InProcessHookBus(log);
        var context = LoaderModContext.Factory(log, bus, ModsDir)("mymod");

        int? seen = null;
        context.Hooks.Subscribe<RoundStartedContext>(c => seen = c.Round);
        bus.Publish(new RoundStartedContext { Round = 7 });

        Assert.Equal(7, seen);
    }

    [Fact]
    public void HasSubscribers_reflects_subscription_state()
    {
        var bus = new InProcessHookBus(new CollectingLog());

        Assert.False(bus.HasSubscribers<RoundStartedContext>());
        var sub = bus.Subscribe<RoundStartedContext>(_ => { });
        Assert.True(bus.HasSubscribers<RoundStartedContext>());
        sub.Dispose();
        Assert.False(bus.HasSubscribers<RoundStartedContext>());
    }

    [Fact]
    public void Hooks_object_payload_context_round_trips()
    {
        var log = new CollectingLog();
        var bus = new InProcessHookBus(log);

        object victim = "the-victim";
        object? received = null;
        bus.Subscribe<EntityDiedContext>(c => received = c.Victim);
        bus.Publish(new EntityDiedContext { Victim = victim });

        Assert.Same(victim, received);
    }

    [Fact]
    public void Hooks_one_subscriber_throwing_does_not_stop_others()
    {
        var log = new CollectingLog();
        var bus = new InProcessHookBus(log);

        bus.Subscribe<RoundStartedContext>(_ => throw new InvalidOperationException("boom"));
        var fired = false;
        bus.Subscribe<RoundStartedContext>(_ => fired = true);

        bus.Publish(new RoundStartedContext { Round = 1 });

        Assert.True(fired);
        Assert.Contains(log.Errors, e => e.Contains("RoundStartedContext") && e.Contains("threw"));
    }

    private sealed class FakeAssets : IModAssets
    {
        public IReadOnlyList<string> Names { get; } = new[] { "probe" };
        public T Load<T>(string name) where T : class => null!;
        public bool TryLoad<T>(string name, out T asset) where T : class { asset = null!; return false; }
    }

    [Fact]
    public void Context_assets_default_to_an_empty_view()
    {
        var host = NewHost(out _);
        var assets = host.ContextFor("mymod").Assets;

        Assert.NotNull(assets);
        Assert.Empty(assets.Names);
        Assert.Null(assets.Load<string>("anything"));
        Assert.False(assets.TryLoad<string>("anything", out _));
    }

    [Fact]
    public void Context_assets_come_from_the_bound_provider()
    {
        var log = new CollectingLog();
        var probe = new FakeAssets();
        var context = LoaderModContext.Factory(log, new InProcessHookBus(log), ModsDir, _ => probe)("mymod");

        Assert.Same(probe, context.Assets);
    }

    private sealed class FakeCoroutineDriver
    {
        public readonly List<IEnumerator> Started = new();
        public readonly List<object> Stopped = new();

        public object Start(IEnumerator routine) { Started.Add(routine); return routine; }
        public void Stop(object token) => Stopped.Add(token);

        public static void PumpToCompletion(object handle)
        {
            var e = (IEnumerator)handle;
            while (e.MoveNext()) { }
        }
    }

    [Fact]
    public void Coroutine_start_of_null_is_a_no_op()
    {
        var driver = new FakeCoroutineDriver();
        var runner = new ModCoroutineRunner("mymod", driver.Start, driver.Stop, new CollectingLog());

        Assert.Null(runner.Start(null));
        Assert.Empty(driver.Started);
    }

    [Fact]
    public void Coroutine_StopAll_stops_a_running_routine()
    {
        var driver = new FakeCoroutineDriver();
        var runner = new ModCoroutineRunner("mymod", driver.Start, driver.Stop, new CollectingLog());

        var handle = runner.Start(Forever());
        runner.StopAll();

        Assert.Contains(handle, driver.Stopped);

        static IEnumerator Forever() { while (true) yield return null; }
    }

    [Fact]
    public void Coroutine_completion_prunes_the_handle()
    {
        var driver = new FakeCoroutineDriver();
        var runner = new ModCoroutineRunner("mymod", driver.Start, driver.Stop, new CollectingLog());

        var handle = runner.Start(Once());
        FakeCoroutineDriver.PumpToCompletion(handle);
        runner.StopAll();

        Assert.Empty(driver.Stopped);

        static IEnumerator Once() { yield return null; }
    }

    [Fact]
    public void Coroutine_that_completes_synchronously_in_start_is_not_tracked()
    {
        var stopped = new List<object>();
        // A driver that runs the routine to completion synchronously inside Start
        // (rather than scheduling it) makes the guard's finally run before Start can
        // record the handle; the finished routine must not be left tracked.
        object SyncStart(IEnumerator routine)
        {
            while (routine.MoveNext()) { }
            return routine;
        }
        var runner = new ModCoroutineRunner("mymod", SyncStart, stopped.Add, new CollectingLog());

        runner.Start(Empty());
        runner.StopAll();

        Assert.Empty(stopped);

        static IEnumerator Empty() { yield break; }
    }

    [Fact]
    public void Coroutine_exception_is_logged_against_the_mod()
    {
        var driver = new FakeCoroutineDriver();
        var log = new CollectingLog();
        var runner = new ModCoroutineRunner("mymod", driver.Start, driver.Stop, log);

        var handle = runner.Start(Boom());
        FakeCoroutineDriver.PumpToCompletion(handle);

        Assert.Contains(log.Errors, e => e.Contains("mymod") && e.Contains("coroutine"));

        static IEnumerator Boom()
        {
            yield return null;
            throw new InvalidOperationException("boom");
        }
    }

    [Fact]
    public void Context_coroutines_default_to_a_noop_view()
    {
        var host = NewHost(out _);
        var coroutines = host.ContextFor("mymod").Coroutines;

        Assert.NotNull(coroutines);
        Assert.Null(coroutines.Start(EmptyRoutine()));

        static IEnumerator EmptyRoutine() { yield break; }
    }

    [Fact]
    public void Patch_registry_postfix_receives_instance_and_args()
    {
        var registry = new ModPatchRegistry();
        PatchInfo? seen = null;
        registry.Add(ModPatchRegistry.Kind.Postfix, "key", "mymod", "T.M", info => seen = info, new CollectingLog());

        object instance = "recv";
        registry.DispatchPostfix("key", instance, new object[] { 1, "two" });

        Assert.NotNull(seen);
        Assert.Same(instance, seen!.Instance);
        Assert.Equal(2, seen.Args.Count);
        Assert.Equal(1, seen.Args[0]);
    }

    [Fact]
    public void Patch_registry_prefix_skip_stops_the_original()
    {
        var registry = new ModPatchRegistry();
        registry.Add(ModPatchRegistry.Kind.Prefix, "key", "mymod", "T.M", info => info.Skip = true, new CollectingLog());

        Assert.False(registry.DispatchPrefix("key", null, Array.Empty<object>()));
    }

    [Fact]
    public void Patch_registry_runs_the_original_without_a_prefix_skip()
    {
        var registry = new ModPatchRegistry();
        Assert.True(registry.DispatchPrefix("unregistered", null, Array.Empty<object>()));

        registry.Add(ModPatchRegistry.Kind.Prefix, "key", "mymod", "T.M", _ => { }, new CollectingLog());
        Assert.True(registry.DispatchPrefix("key", null, Array.Empty<object>()));
    }

    [Fact]
    public void Patch_registry_isolates_a_throwing_handler()
    {
        var registry = new ModPatchRegistry();
        var log = new CollectingLog();
        var secondRan = false;
        registry.Add(ModPatchRegistry.Kind.Postfix, "key", "badmod", "T.M", _ => throw new InvalidOperationException("boom"), log);
        registry.Add(ModPatchRegistry.Kind.Postfix, "key", "goodmod", "T.M", _ => secondRan = true, log);

        registry.DispatchPostfix("key", null, Array.Empty<object>());

        Assert.True(secondRan);
        Assert.Contains(log.Errors, e => e.Contains("badmod") && e.Contains("patch"));
    }

    [Fact]
    public void Patch_registry_remove_mod_drops_its_handlers()
    {
        var registry = new ModPatchRegistry();
        var ran = false;
        registry.Add(ModPatchRegistry.Kind.Postfix, "key", "mymod", "T.M", _ => ran = true, new CollectingLog());
        registry.RemoveMod("mymod");

        registry.DispatchPostfix("key", null, Array.Empty<object>());

        Assert.False(ran);
    }

    [Fact]
    public void Context_patches_default_to_a_noop_view()
    {
        var host = NewHost(out _);
        var patches = host.ContextFor("mymod").Patches;

        Assert.NotNull(patches);
        patches.Postfix("T", "M", _ => { });
    }

    [Fact]
    public void ModContext_For_delegates_to_the_bound_resolver()
    {
        var host = NewHost(out _);
        var expected = host.ContextFor("mymod");
        ModContext.BindResolver(_ => expected);
        try
        {
            Assert.Same(expected, ModContext.For(new object()));
        }
        finally
        {
            ModContext.BindResolver(null);
        }
    }
}
