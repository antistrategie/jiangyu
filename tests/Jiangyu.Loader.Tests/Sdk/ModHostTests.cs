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

    private sealed class GoodMod : JiangyuSystem
    {
        public int Inits;
        public int Scenes;
        public int Unloads;
        public int TemplatesApplied;

        public override void OnInit() => Inits++;
        public override void OnTemplatesApplied() => TemplatesApplied++;
        public override void OnSceneLoaded(int buildIndex, string sceneName) => Scenes++;
        public override void OnUnload() => Unloads++;
    }

    private sealed class ThrowingMod : JiangyuSystem
    {
        public int Attempts;

        public override void OnSceneLoaded(int buildIndex, string sceneName)
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
            host.SceneLoaded(0, "Tactical"); // the ThrowingMod entry point quarantines after 3 throws

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
            host.SceneLoaded(0, "Tactical");

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
        host.UnloadAll();

        Assert.Equal(1, mod.Inits);
        Assert.Equal(1, mod.TemplatesApplied);
        Assert.Equal(1, mod.Scenes);
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

        host.SceneLoaded(0, "Tactical");

        Assert.Equal(1, good.Scenes);
        Assert.Equal(1, bad.Attempts);
        Assert.Contains(log.Errors, e => e.Contains("bad") && e.Contains("OnSceneLoaded"));
    }

    [Fact]
    public void Repeated_failures_quarantine_the_mod()
    {
        var host = NewHost(out var log);
        var bad = new ThrowingMod();
        var loaded = host.Adopt("bad", bad);

        for (var i = 0; i < 5; i++)
            host.SceneLoaded(0, "Tactical");

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
    public void Register_discovers_concrete_systems()
    {
        var host = NewHost(out _);
        var registered = host.Register(typeof(GoodMod).Assembly, "mymod");

        Assert.Contains(registered, s => s.Instance is GoodMod);
    }

    // --- [DependsOn] ordering ---
    //
    // OrderByDependencies is the algorithm Register orders a mod's systems by before
    // it instantiates them, so a dependency's lifecycle runs before its dependents'.
    // These exercise it directly with plain marker classes carrying [DependsOn]: the
    // algorithm orders any type list by its attributes, and keeping them out of the
    // JiangyuSystem hierarchy stops assembly-wide discovery picking them up elsewhere.

    private sealed class OrderA { }
    [DependsOn(typeof(OrderA))] private sealed class OrderB { }
    [DependsOn(typeof(OrderB))] private sealed class OrderC { }
    private sealed class IndepAlpha { }
    private sealed class IndepZeta { }
    [DependsOn(typeof(OrderA))] private sealed class NeedsAbsent { }
    [DependsOn(typeof(SelfRef))] private sealed class SelfRef { }
    [DependsOn(typeof(CycleTwo))] private sealed class CycleOne { }
    [DependsOn(typeof(CycleOne))] private sealed class CycleTwo { }

    [Fact]
    public void OrderByDependencies_places_dependencies_before_their_dependents()
    {
        var host = NewHost(out _);

        var ordered = host.OrderByDependencies(new[] { typeof(OrderC), typeof(OrderA), typeof(OrderB) }, "mod");

        Assert.Equal(new[] { typeof(OrderA), typeof(OrderB), typeof(OrderC) }, ordered);
    }

    [Fact]
    public void OrderByDependencies_orders_independent_systems_by_name()
    {
        var host = NewHost(out _);

        var ordered = host.OrderByDependencies(new[] { typeof(IndepZeta), typeof(IndepAlpha) }, "mod");

        Assert.Equal(new[] { typeof(IndepAlpha), typeof(IndepZeta) }, ordered);
    }

    [Fact]
    public void OrderByDependencies_warns_and_keeps_a_system_whose_dependency_is_outside_the_mod()
    {
        var host = NewHost(out var log);

        var ordered = host.OrderByDependencies(new[] { typeof(NeedsAbsent) }, "mod");

        Assert.Contains(typeof(NeedsAbsent), ordered);
        Assert.Contains(log.Warns, w => w.Contains("not a system of this mod"));
    }

    [Fact]
    public void OrderByDependencies_warns_and_keeps_a_self_dependency()
    {
        var host = NewHost(out var log);

        var ordered = host.OrderByDependencies(new[] { typeof(SelfRef) }, "mod");

        Assert.Contains(typeof(SelfRef), ordered);
        Assert.Contains(log.Warns, w => w.Contains("depends on itself"));
    }

    [Fact]
    public void OrderByDependencies_breaks_a_cycle_by_name_and_warns()
    {
        var host = NewHost(out var log);

        var ordered = host.OrderByDependencies(new[] { typeof(CycleTwo), typeof(CycleOne) }, "mod");

        Assert.Equal(new[] { typeof(CycleOne), typeof(CycleTwo) }, ordered);
        Assert.Contains(log.Warns, w => w.Contains("cycle"));
    }

    // Real discovered systems with a dependency chain, to confirm the ordering flows
    // through Register into registration (and therefore lifecycle dispatch) order.
    // Register scans the whole test assembly, so other systems interleave: assert the
    // relative order of these three only.
    private sealed class FirstSystem : JiangyuSystem { }
    [DependsOn(typeof(FirstSystem))] private sealed class SecondSystem : JiangyuSystem { }
    [DependsOn(typeof(SecondSystem))] private sealed class ThirdSystem : JiangyuSystem { }

    [Fact]
    public void Register_orders_dependencies_before_their_dependents()
    {
        var host = NewHost(out _);
        var registered = host.Register(typeof(FirstSystem).Assembly, "ordered");

        int IndexOf<T>()
        {
            for (var i = 0; i < registered.Count; i++)
                if (registered[i].Instance is T)
                    return i;
            return -1;
        }

        var first = IndexOf<FirstSystem>();
        var second = IndexOf<SecondSystem>();
        var third = IndexOf<ThirdSystem>();

        Assert.True(first >= 0 && second >= 0 && third >= 0, "all three systems register");
        Assert.True(first < second, "FirstSystem registers before its dependent SecondSystem");
        Assert.True(second < third, "SecondSystem registers before its dependent ThirdSystem");
    }

    [DependsOn(typeof(OrderA), null)] private sealed class HasNullDep { }

    [Fact]
    public void OrderByDependencies_warns_and_keeps_a_system_with_a_null_dependency_entry()
    {
        var host = NewHost(out var log);

        // A null entry must not throw (otherwise the NRE escapes Register and aborts
        // the whole mod's DLL load). OrderA is passed so only the null entry warns.
        var ordered = host.OrderByDependencies(new[] { typeof(HasNullDep), typeof(OrderA) }, "mod");

        Assert.Contains(typeof(HasNullDep), ordered);
        Assert.Contains(log.Warns, w => w.Contains("null"));
    }

    [Fact]
    public void OrderByDependencies_deduplicates_repeated_input_without_a_false_cycle()
    {
        var host = NewHost(out var log);

        var ordered = host.OrderByDependencies(new[] { typeof(OrderA), typeof(OrderA) }, "mod");

        Assert.Equal(new[] { typeof(OrderA) }, ordered);
        Assert.DoesNotContain(log.Warns, w => w.Contains("cycle"));
    }

    // No parameterless ctor, so assembly-wide discovery (ConcreteSystems) skips it;
    // Adopt takes the constructed instance, letting the test fix the registration order.
    private sealed class RecordingSystem : JiangyuSystem
    {
        private readonly List<string> _order;
        private readonly string _id;
        public RecordingSystem(List<string> order, string id) { _order = order; _id = id; }
        public override void OnUnload() => _order.Add(_id);
    }

    [Fact]
    public void UnloadAll_tears_systems_down_in_reverse_registration_order()
    {
        var host = NewHost(out _);
        var order = new List<string>();
        host.Adopt("m", new RecordingSystem(order, "A"));
        host.Adopt("m", new RecordingSystem(order, "B"));
        host.Adopt("m", new RecordingSystem(order, "C"));

        host.UnloadAll();

        Assert.Equal(new[] { "C", "B", "A" }, order);
    }

    [DependsOn(typeof(OrderA))] private class BaseDep { }
    private sealed class DerivedDep : BaseDep { }

    [Fact]
    public void OrderByDependencies_inherits_DependsOn_from_a_base_system()
    {
        var host = NewHost(out _);

        var ordered = host.OrderByDependencies(new[] { typeof(DerivedDep), typeof(OrderA) }, "mod");

        // DerivedDep inherits BaseDep's [DependsOn(OrderA)] (Inherited = true), so OrderA orders first.
        Assert.Equal(new[] { typeof(OrderA), typeof(DerivedDep) }, ordered);
    }

    [Fact]
    public void Register_orders_systems_across_a_mods_assemblies_and_dedupes_overlap()
    {
        var host = NewHost(out _);

        // A real multi-DLL mod passes its distinct code assemblies; the same assembly
        // twice exercises both the multi-assembly overload and the dedup.
        var registered = host.Register(new[] { typeof(GoodMod).Assembly, typeof(GoodMod).Assembly }, "multi");

        Assert.Contains(registered, s => s.Instance is GoodMod);
        Assert.Single(registered, s => s.Instance is FirstSystem);
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
