using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MelonLogger = MelonLoader.MelonLogger;

namespace Jiangyu.Loader.Diagnostics;

// Dev command (rngtrace): a draw-level census of the game's two RNG streams. Harmony
// postfixes on UnityEngine.Random (the global frame-shared stream) and
// Il2CppMenace.Tools.PseudoRandom (the game's seeded per-owner streams) record every
// draw while enabled: sequence, frame, api, arguments, result, and for PseudoRandom
// the owning instance pointer, so distinct streams separate. Two processes' traces
// diff by sequence to the first divergent draw, which localises an RNG consumer far
// more precisely than barrier-hash comparison. A managed hook cannot see its native
// caller, so attribution runs on draw order and stream identity, not call sites.
//   start {cap:N}   install hooks (once) and begin capturing (ring buffer, default 4096)
//   stop            stop capturing (hooks stay installed, dormant)
//   dump {tail:N}   the last N captured draws (default 256)
//   status          enabled, captured, dropped, cap
//   census          every native method that references each RNG entry point, from
//                   the xref table MelonLoader precomputed at assembly generation
//                   (XrefScanner.UsedBy); needs no gameplay, the title screen is enough
//   pin {enabled}   force Vector2Extensions.RandomPositiveBetweenXY to its range
//                   midpoint. Element aim/movement delays route through it off the
//                   global stream; pinning makes element timing deterministic, which
//                   tests whether timing-driven reordering of the shared seeded
//                   stream is the combat desync mechanism
internal static class RngTraceProbe
{
    private sealed class Draw
    {
        public long Seq;
        public int Frame;
        public string Api;
        public string Args;
        public string Result;
        public string Owner;
    }

    private static readonly object Sync = new();
    private static readonly List<Draw> Buffer = new();
    private static bool _hooked;
    private static volatile bool _enabled;
    private static long _seq;
    private static long _dropped;
    private static int _cap = 4096;
    private static string _hookErrors;

    public static object Run(JsonElement args, MelonLogger.Instance log)
    {
        var op = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("op", out var o)
            ? o.GetString()
            : null;
        try
        {
            switch (op)
            {
                case "start":
                    if (args.TryGetProperty("cap", out var cap) && cap.TryGetInt32(out var capValue) && capValue > 0)
                        _cap = capValue;
                    EnsureHooks(log);
                    lock (Sync)
                    {
                        Buffer.Clear();
                        _seq = 0;
                        _dropped = 0;
                    }

                    _enabled = true;
                    return new { ok = true, cap = _cap, hookErrors = _hookErrors };
                case "stop":
                    _enabled = false;
                    lock (Sync)
                    {
                        return new { ok = true, captured = _seq, dropped = _dropped };
                    }

                case "status":
                    lock (Sync)
                    {
                        return new { ok = true, enabled = _enabled, captured = _seq, dropped = _dropped, cap = _cap, hookErrors = _hookErrors };
                    }

                case "dump":
                    var tail = args.TryGetProperty("tail", out var t) && t.TryGetInt32(out var tailValue) && tailValue > 0
                        ? tailValue
                        : 256;
                    lock (Sync)
                    {
                        var skip = Math.Max(0, Buffer.Count - tail);
                        return new
                        {
                            ok = true,
                            captured = _seq,
                            dropped = _dropped,
                            draws = Buffer.Skip(skip).Select(d => new
                            {
                                seq = d.Seq,
                                frame = d.Frame,
                                api = d.Api,
                                args = d.Args,
                                result = d.Result,
                                owner = d.Owner,
                            }).ToArray(),
                        };
                    }

                case "census":
                    return Census();
                case "pin":
                    return Pin(args, log);
                case "owners":
                    return Owners();
                case "xref":
                    return Xref(args);
                default:
                    return new { error = "rngtrace op must be start | stop | dump | status | census | pin | owners | xref" };
            }
        }
        catch (Exception ex)
        {
            return new { error = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    private static void Note(string api, string arguments, string result, string owner)
    {
        if (!_enabled)
            return;
        int frame;
        try
        {
            frame = UnityEngine.Time.frameCount;
        }
        catch
        {
            frame = -1;
        }

        lock (Sync)
        {
            if (Buffer.Count >= _cap)
            {
                _dropped++;
                _seq++;
                return;
            }

            Buffer.Add(new Draw { Seq = _seq++, Frame = frame, Api = api, Args = arguments, Result = result, Owner = owner });
        }
    }

    // One curated hook per draw entry point. A failed bind is reported, not fatal:
    // partial coverage still traces, and the failure text names what is missing.
    private static void EnsureHooks(MelonLogger.Instance log)
    {
        if (_hooked)
            return;
        _hooked = true;
        var harmony = new HarmonyLib.Harmony("jiangyu.dev.rngtrace");
        var failures = new List<string>();

        void Hook(Type target, string name, Type[] signature, string postfixName)
        {
            try
            {
                var method = AccessTools.Method(target, name, signature);
                if (method == null)
                {
                    failures.Add($"{target.Name}.{name} not found");
                    return;
                }

                harmony.Patch(method, postfix: new HarmonyMethod(typeof(RngTraceProbe), postfixName));
            }
            catch (Exception ex)
            {
                failures.Add($"{target.Name}.{name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var unity = typeof(UnityEngine.Random);
        Hook(unity, "Range", new[] { typeof(float), typeof(float) }, nameof(UnityRangeFloat));
        Hook(unity, "Range", new[] { typeof(int), typeof(int) }, nameof(UnityRangeInt));
        Hook(unity, "get_value", Type.EmptyTypes, nameof(UnityValue));
        Hook(unity, "InitState", new[] { typeof(int) }, nameof(UnityInitState));

        // The sim-side funnel over the global stream: the census shows no combat
        // method calling UnityEngine.Random directly, so deviation-style rolls route
        // through this helper. Tracing it ties a global-stream draw to a sim value.
        Hook(typeof(Il2CppMenace.Tools.Math), "Deviate", new[] { typeof(float), typeof(float) }, nameof(MathDeviate));

        var pseudo = typeof(Il2CppMenace.Tools.PseudoRandom);
        Hook(pseudo, "NextUInt32", Type.EmptyTypes, nameof(PseudoNextUInt32));
        Hook(pseudo, "Next", Type.EmptyTypes, nameof(PseudoNext));
        Hook(pseudo, "Next", new[] { typeof(int) }, nameof(PseudoNextMax));
        Hook(pseudo, "Next", new[] { typeof(int), typeof(int) }, nameof(PseudoNextMinMax));
        Hook(pseudo, "Range", new[] { typeof(float), typeof(float) }, nameof(PseudoRangeFloat));
        Hook(pseudo, "Range", new[] { typeof(int), typeof(int) }, nameof(PseudoRangeInt));
        Hook(pseudo, "NextBool", Type.EmptyTypes, nameof(PseudoNextBool));
        Hook(pseudo, "NextSeed", Type.EmptyTypes, nameof(PseudoNextSeed));
        Hook(pseudo, "Init", new[] { typeof(int) }, nameof(PseudoInit));

        _hookErrors = failures.Count == 0 ? null : string.Join("; ", failures);
        if (_hookErrors != null)
            log?.Warning($"[rngtrace] partial hooks: {_hookErrors}");
    }

    // The complete caller list per RNG entry point, read from the xref scan results
    // MelonLoader baked into the interop assemblies (CachedScanResults). Coverage is
    // total over the shipped native code, independent of what any script exercises;
    // an xref that fails to resolve back to a managed wrapper is counted, not named.
    private static object Census()
    {
        var methods = new List<object>();
        foreach (var (label, method) in CensusTargets())
            methods.Add(CallersOf(label, method));
        return new { ok = true, methods };
    }

    private static object CallersOf(string label, MethodBase method)
    {
        if (method == null)
            return new { api = label, error = "method not found on the wrapper" };
        try
        {
            var callers = new SortedSet<string>(StringComparer.Ordinal);
            var unresolved = 0;
            foreach (var xref in Il2CppInterop.Common.XrefScans.XrefScanner.UsedBy(method))
            {
                MethodBase resolved = null;
                try
                {
                    resolved = Il2CppInterop.Runtime.XrefScans.XrefInstanceExtensions.TryResolve(xref);
                }
                catch
                {
                }

                if (resolved == null)
                    unresolved++;
                else
                    callers.Add($"{resolved.DeclaringType?.FullName}.{resolved.Name}");
            }

            return new { api = label, callers = callers.ToArray(), unresolved };
        }
        catch (Exception ex)
        {
            return new { api = label, error = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    // On-demand caller census of an arbitrary game method: every overload named
    // `method` on `type` gets its own caller list. Confirms whether a dispatch
    // entry (e.g. Skill.Use, Actor.MoveTo) is the sole route commands take.
    private static object Xref(JsonElement args)
    {
        var typeName = args.TryGetProperty("type", out var t) ? t.GetString() : null;
        var methodName = args.TryGetProperty("method", out var m) ? m.GetString() : null;
        if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName))
            return new { error = "xref needs args.type (full il2cpp type name) and args.method" };
        var type = AccessTools.TypeByName(typeName);
        if (type == null)
            return new { error = $"type '{typeName}' not found" };
        var overloads = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(x => x.Name == methodName)
            .ToArray();
        if (overloads.Length == 0)
            return new { error = $"no method '{methodName}' declared on '{typeName}'" };
        var results = overloads
            .Select(x => CallersOf($"{typeName}.{methodName}({string.Join(",", x.GetParameters().Select(p => p.ParameterType.Name))})", x))
            .ToArray();
        return new { ok = true, methods = results };
    }

    private static IEnumerable<(string Label, MethodBase Method)> CensusTargets()
    {
        var unity = typeof(UnityEngine.Random);
        yield return ("Random.Range(float)", AccessTools.Method(unity, "Range", new[] { typeof(float), typeof(float) }));
        yield return ("Random.Range(int)", AccessTools.Method(unity, "Range", new[] { typeof(int), typeof(int) }));
        yield return ("Random.RandomRangeInt", AccessTools.Method(unity, "RandomRangeInt", new[] { typeof(int), typeof(int) }));
        yield return ("Random.get_value", AccessTools.Method(unity, "get_value", Type.EmptyTypes));
        yield return ("Random.InitState", AccessTools.Method(unity, "InitState", new[] { typeof(int) }));
        yield return ("Random.get_insideUnitSphere", AccessTools.Method(unity, "get_insideUnitSphere", Type.EmptyTypes));
        yield return ("Random.get_insideUnitCircle", AccessTools.Method(unity, "get_insideUnitCircle", Type.EmptyTypes));
        yield return ("Random.get_onUnitSphere", AccessTools.Method(unity, "get_onUnitSphere", Type.EmptyTypes));
        yield return ("Random.get_rotation", AccessTools.Method(unity, "get_rotation", Type.EmptyTypes));

        // Second-tier helpers: sim code reaches the global stream through these, so
        // their callers are the actual desync surface.
        yield return ("Math.Deviate", AccessTools.Method(typeof(Il2CppMenace.Tools.Math), "Deviate", new[] { typeof(float), typeof(float) }));
        yield return ("Vector2Extensions.RandomPositiveBetweenXY", AccessTools.Method(
            typeof(Il2CppMenace.Tools.Vector2Extensions), "RandomPositiveBetweenXY", new[] { typeof(UnityEngine.Vector2) }));

        var pseudo = typeof(Il2CppMenace.Tools.PseudoRandom);
        yield return ("PseudoRandom.NextUInt32", AccessTools.Method(pseudo, "NextUInt32", Type.EmptyTypes));
        yield return ("PseudoRandom.Next", AccessTools.Method(pseudo, "Next", Type.EmptyTypes));
        yield return ("PseudoRandom.Next(max)", AccessTools.Method(pseudo, "Next", new[] { typeof(int) }));
        yield return ("PseudoRandom.Next(min,max)", AccessTools.Method(pseudo, "Next", new[] { typeof(int), typeof(int) }));
        yield return ("PseudoRandom.Range(float)", AccessTools.Method(pseudo, "Range", new[] { typeof(float), typeof(float) }));
        yield return ("PseudoRandom.Range(int)", AccessTools.Method(pseudo, "Range", new[] { typeof(int), typeof(int) }));
        yield return ("PseudoRandom.NextBool", AccessTools.Method(pseudo, "NextBool", Type.EmptyTypes));
        yield return ("PseudoRandom.NextSeed", AccessTools.Method(pseudo, "NextSeed", Type.EmptyTypes));
        yield return ("PseudoRandom.Init", AccessTools.Method(pseudo, "Init", new[] { typeof(int) }));

        // Construction sites: an unseeded ctor caller creates a process-varying stream,
        // which is the nondeterminism the trace diff localises.
        yield return ("PseudoRandom..ctor()", AccessTools.Constructor(pseudo, Type.EmptyTypes));
        yield return ("PseudoRandom..ctor(int)", AccessTools.Constructor(pseudo, new[] { typeof(int) }));
    }

    // Resolves the known PseudoRandom holders (from the interop member scan) to live
    // instance pointers, so trace owners map to named streams. Holders that are plain
    // il2cpp objects with no scene presence or reachable singleton are best-effort.
    private static object Owners()
    {
        var owners = new List<object>();

        void AddStatic(string name, Func<Il2CppMenace.Tools.PseudoRandom> get)
        {
            try
            {
                var rng = get();
                owners.Add(new { name, ptr = rng == null ? null : Ptr(rng) });
            }
            catch (Exception ex)
            {
                owners.Add(new { name, error = $"{ex.GetType().Name}: {ex.Message}" });
            }
        }

        AddStatic("TacticalManager.s_Random", () => Il2CppMenace.Tactical.TacticalManager.s_Random);
        AddStatic("TacticalBarksManager.m_Random", () => Il2CppMenace.States.TacticalState.Get()?.GetBarks()?.m_Random);
        AddStatic("Ragdoll.RANDOM", () => Il2CppMenace.Tactical.Ragdoll.RANDOM);
        AddStatic("RandomBoredBehaviourEx.RNG", () => Il2CppMenace.Tactical.RandomBoredBehaviourEx.RNG);
        AddStatic("RandomAnimationPicker.RNG", () => Il2CppMenace.Tools.RandomAnimationPicker.RNG);
        AddStatic("GameConditionVars.RANDOM", () => Il2CppMenace.Strategy.GameConditionVars.RANDOM);

        try
        {
            var found = UnityEngine.Object.FindObjectsOfType(
                Il2CppInterop.Runtime.Il2CppType.Of<Il2CppMenace.Tactical.ExpandRetract>());
            foreach (var obj in found)
            {
                var er = obj.TryCast<Il2CppMenace.Tactical.ExpandRetract>();
                if (er == null)
                    continue;
                Il2CppMenace.Tools.PseudoRandom rng = null;
                try { rng = er.m_Random; } catch { }
                owners.Add(new { name = $"ExpandRetract[{er.name}]", ptr = rng == null ? null : Ptr(rng) });
            }
        }
        catch (Exception ex)
        {
            owners.Add(new { name = "ExpandRetract", error = $"{ex.GetType().Name}: {ex.Message}" });
        }

        return new { ok = true, owners };
    }

    private static bool _pinInstalled;
    private static volatile bool _pinEnabled;

    private static object Pin(JsonElement args, MelonLogger.Instance log)
    {
        _pinEnabled = args.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
        if (_pinEnabled && !_pinInstalled)
        {
            var method = AccessTools.Method(
                typeof(Il2CppMenace.Tools.Vector2Extensions), "RandomPositiveBetweenXY", new[] { typeof(UnityEngine.Vector2) });
            if (method == null)
                return new { error = "RandomPositiveBetweenXY not found on the wrapper" };
            new HarmonyLib.Harmony("jiangyu.dev.rngpin")
                .Patch(method, prefix: new HarmonyMethod(typeof(RngTraceProbe), nameof(PinPrefix)));
            _pinInstalled = true;
            log?.Msg("[rngtrace] RandomPositiveBetweenXY pinned to range midpoint");
        }

        return new { ok = true, pinned = _pinEnabled };
    }

    private static bool PinPrefix(UnityEngine.Vector2 _self, ref float __result)
    {
        if (!_pinEnabled)
            return true;
        __result = (_self.x + _self.y) / 2f;
        return false;
    }

    private static string Ptr(Il2CppMenace.Tools.PseudoRandom instance)
    {
        try
        {
            return instance.Pointer.ToString("x");
        }
        catch
        {
            return "?";
        }
    }

    private static void UnityRangeFloat(float minInclusive, float maxInclusive, float __result)
        => Note("Random.Range(float)", $"{minInclusive},{maxInclusive}", __result.ToString("R"), null);

    private static void UnityRangeInt(int minInclusive, int maxExclusive, int __result)
        => Note("Random.Range(int)", $"{minInclusive},{maxExclusive}", __result.ToString(), null);

    private static void UnityValue(float __result)
        => Note("Random.value", null, __result.ToString("R"), null);

    private static void UnityInitState(int seed)
        => Note("Random.InitState", seed.ToString(), null, null);

    private static void MathDeviate(float _value, float _deviation, float __result)
        => Note("Math.Deviate", $"{_value},{_deviation}", __result.ToString("R"), null);

    private static void PseudoNextUInt32(Il2CppMenace.Tools.PseudoRandom __instance, uint __result)
        => Note("PseudoRandom.NextUInt32", null, __result.ToString(), Ptr(__instance));

    private static void PseudoNext(Il2CppMenace.Tools.PseudoRandom __instance, int __result)
        => Note("PseudoRandom.Next", null, __result.ToString(), Ptr(__instance));

    private static void PseudoNextMax(Il2CppMenace.Tools.PseudoRandom __instance, int _maxExclusive, int __result)
        => Note("PseudoRandom.Next(max)", _maxExclusive.ToString(), __result.ToString(), Ptr(__instance));

    private static void PseudoNextMinMax(Il2CppMenace.Tools.PseudoRandom __instance, int _minInclusive, int _maxExclusive, int __result)
        => Note("PseudoRandom.Next(min,max)", $"{_minInclusive},{_maxExclusive}", __result.ToString(), Ptr(__instance));

    private static void PseudoRangeFloat(Il2CppMenace.Tools.PseudoRandom __instance, float _min, float _max, float __result)
        => Note("PseudoRandom.Range(float)", $"{_min},{_max}", __result.ToString("R"), Ptr(__instance));

    private static void PseudoRangeInt(Il2CppMenace.Tools.PseudoRandom __instance, int _minInclusive, int _maxInclusive, int __result)
        => Note("PseudoRandom.Range(int)", $"{_minInclusive},{_maxInclusive}", __result.ToString(), Ptr(__instance));

    private static void PseudoNextBool(Il2CppMenace.Tools.PseudoRandom __instance, bool __result)
        => Note("PseudoRandom.NextBool", null, __result.ToString(), Ptr(__instance));

    private static void PseudoNextSeed(Il2CppMenace.Tools.PseudoRandom __instance, int __result)
        => Note("PseudoRandom.NextSeed", null, __result.ToString(), Ptr(__instance));

    private static void PseudoInit(Il2CppMenace.Tools.PseudoRandom __instance, int _seed)
        => Note("PseudoRandom.Init", _seed.ToString(), null, Ptr(__instance));
}
