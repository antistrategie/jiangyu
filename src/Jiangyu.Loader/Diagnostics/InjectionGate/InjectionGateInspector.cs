using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;
using Il2CppMenace.Tactical.Skills.Effects;
using Il2CppSirenix.Serialization;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace Jiangyu.Loader.Diagnostics.InjectionGate;

/// <summary>
/// The Phase 0 injection go/no-go, run as a re-runnable diagnostic rather than
/// throwaway spike code. Gated by a <c>jiangyu-gate.flag</c> file in
/// <c>&lt;UserData&gt;</c> so it never runs in a normal session. Writes a
/// timestamped JSON report alongside the other inspector dumps and logs a
/// per-check summary to the MelonLoader console.
///
/// <para>Two phases. The structural phase needs no mission and runs at loader
/// init: type registration, runtime assignability, the missing-bind probe, and
/// a blank-template allocation. The live phase needs an active actor in a
/// Tactical mission and re-establishes the append-not-replace and game-dispatch
/// findings. The two hardest checks (damage-clamp honoured, Odin save/load
/// survival) are recorded as pending with their exact procedure because they
/// are best driven and observed against the running game.</para>
/// </summary>
internal static class InjectionGateInspector
{
    private const string FlagFileName = "jiangyu-gate.flag";

    // The damage check drives a self-hit through the real damage pipeline, the
    // riskiest operation in the harness. It is opt-in behind its own flag so the
    // default armed run proves dispatch without it.
    private const string DamageFlagFileName = "jiangyu-gate-damage.flag";

    private const string Pass = "pass";
    private const string Fail = "fail";
    private const string Error = "error";
    private const string Skipped = "skipped";

    public static bool IsEnabled()
    {
        try
        {
            return File.Exists(System.IO.Path.Combine(MelonEnvironment.UserDataDirectory, FlagFileName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Structural phase. Safe anywhere, no mission required.</summary>
    public static void Run(string sceneTag, bool live, MelonLogger.Instance log)
    {
        if (!IsEnabled())
            return;

        var report = new GateReport
        {
            Timestamp = DateTimeOffset.UtcNow,
            SceneTag = sceneTag,
            Live = live,
            SdkLoaderVersion = BuildInfo.Version,
            GameVersion = SafeGameVersion(),
        };

        // Emit logs each result the instant it completes, so a later native
        // crash still leaves a per-check trail in Latest.log. The order runs the
        // safe checks first and the riskiest (the self-hit) last.
        Emit(report, CheckRegister(), log);
        Emit(report, CheckAssignable(), log);
        Emit(report, CheckMissingBindProbe(), log);
        Emit(report, CheckBlankSkillTemplate(), log);
        Emit(report, CheckTemplateOverrideSurface(), log);
        Emit(report, CheckValueProviderDispatch(), log);
        Emit(report, CheckOdinSerialisationSurvival(), log);
        Emit(report, CheckDispatchToInjected(live), log);
        Emit(report, CheckDamageClampHonoured(live), log);

        Write(report, sceneTag, log);
    }

    /// <summary>
    /// Live phase trigger. Returns false when there is no active actor yet so the
    /// caller can retry on a later frame, true once the live run has happened.
    /// </summary>
    public static bool RunLiveIfReady(string sceneTag, MelonLogger.Instance log)
    {
        if (!IsEnabled())
            return true;

        if (!TryGetActiveActor(out _))
            return false;

        Run(sceneTag, live: true, log);
        return true;
    }

    private static GateCheck CheckRegister()
    {
        try
        {
            var ok = GateTypeRegistrar.TryRegister(out var detail, out var perRoot);
            var check = new GateCheck
            {
                Name = "inject.register",
                Status = ok ? Pass : Fail,
                Detail = detail,
            };
            foreach (var kv in perRoot)
                check.Evidence[kv.Key] = kv.Value;
            return check;
        }
        catch (Exception ex)
        {
            return Errored("inject.register", ex);
        }
    }

    private static GateCheck CheckAssignable()
    {
        var check = new GateCheck { Name = "inject.assignable" };
        var allOk = true;

        allOk &= ProbeConstruct(check, "SkillEventHandler", static () =>
        {
            var h = new GateEventHandler();
            return h.Pointer != IntPtr.Zero && h.TryCast<SkillEventHandler>() != null;
        });

        allOk &= ProbeConstruct(check, "TacticalCondition", static () =>
        {
            var c = new GateCondition();
            return c.Pointer != IntPtr.Zero && c.TryCast<TacticalCondition>() != null;
        });

        // A ScriptableObject-rooted type must be created via CreateInstance, not
        // `new`. The `new`/DerivedConstructorBody path yields an invalid object.
        allOk &= ProbeConstruct(check, "SkillEventHandlerTemplate", static () =>
        {
            var t = CreateGateTemplate();
            return t != null && t.Pointer != IntPtr.Zero && t.TryCast<SkillEventHandlerTemplate>() != null;
        });

        // Interface-implementer root: constructs and casts to the IL2CPP interface
        // it was registered against. A null cast means the interface vtable wiring
        // did not take, so the game would never route GetValue to the override.
        allOk &= ProbeConstruct(check, "IValueProvider", static () =>
        {
            var p = new GateValueProvider();
            return p.Pointer != IntPtr.Zero && p.TryCast<IValueProvider>() != null;
        });

        check.Status = allOk ? Pass : Fail;
        return check;
    }

    // Invokes GetValue through the IL2CPP interface vtable (cast to IValueProvider,
    // then call), the native->managed dispatch path the game uses. The sentinel
    // coming back proves the interface method, not just the type, was wired — the
    // part interface-implementer injection leaves uncertain that a plain cast does
    // not. No mission needed.
    private static GateCheck CheckValueProviderDispatch()
    {
        try
        {
            var provider = new GateValueProvider();
            var asInterface = provider.TryCast<IValueProvider>();
            if (asInterface == null)
                return new GateCheck { Name = "valueProvider.interfaceDispatch", Status = Fail, Detail = "GateValueProvider did not cast to IValueProvider" };

            var value = asInterface.GetValue(null, null, null);
            var ok = provider.GetValueCalled && value == GateValueProvider.Sentinel;

            return new GateCheck
            {
                Name = "valueProvider.interfaceDispatch",
                Status = ok ? Pass : Fail,
                Detail = "called GetValue through the IL2CPP IValueProvider interface vtable; the managed override ran and returned its sentinel",
                Evidence =
                {
                    ["getValueCalled"] = provider.GetValueCalled,
                    ["returnedValue"] = value,
                    ["expectedSentinel"] = GateValueProvider.Sentinel,
                },
            };
        }
        catch (Exception ex)
        {
            return Errored("valueProvider.interfaceDispatch", ex);
        }
    }

    // Calls the wider override surface on the injected SkillEventHandlerTemplate
    // through the IL2CPP vtable: IsUsable() (usability gate) and
    // ApplyToEntityProperties() (passive stat effects). Reaching the managed
    // overrides proves a modder can author both shapes on the proven template root,
    // not just the Create() factory.
    private static GateCheck CheckTemplateOverrideSurface()
    {
        try
        {
            var template = CreateGateTemplate();
            if (template == null)
                return new GateCheck { Name = "template.overrideSurface", Status = Fail, Detail = "could not construct GateHandlerTemplate" };

            var usable = template.IsUsable();
            template.ApplyToEntityProperties(null);
            var ok = template.IsUsableCalled && template.ApplyPropsCalled;

            return new GateCheck
            {
                Name = "template.overrideSurface",
                Status = ok ? Pass : Fail,
                Detail = "called IsUsable() and ApplyToEntityProperties() on the injected SkillEventHandlerTemplate through the IL2CPP vtable; the managed overrides ran",
                Evidence =
                {
                    ["isUsableReturned"] = usable,
                    ["isUsableCalled"] = template.IsUsableCalled,
                    ["applyPropsCalled"] = template.ApplyPropsCalled,
                },
            };
        }
        catch (Exception ex)
        {
            return Errored("template.overrideSurface", ex);
        }
    }

    private static bool ProbeConstruct(GateCheck check, string root, Func<bool> body)
    {
        try
        {
            var ok = body();
            check.Evidence[root] = ok;
            return ok;
        }
        catch (Exception ex)
        {
            check.Evidence[root] = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    // The structural self-check's lookup primitive is "reflect a declared game
    // member, see whether it is present". This confirms that primitive returns
    // null for an absent member without throwing, so a missing bind surfaces as
    // a detectable absence rather than a hard crash.
    private static GateCheck CheckMissingBindProbe()
    {
        try
        {
            var present = typeof(Skill).GetMethod("OnTurnStart") != null;
            var absent = typeof(Skill).GetMethod("Jiangyu_NoSuchMethod_GateProbe") == null;
            var ok = present && absent;
            return new GateCheck
            {
                Name = "bind.missingProbe",
                Status = ok ? Pass : Fail,
                Detail = "managed reflection returns the present member and null for an absent one, no throw",
                Evidence =
                {
                    ["knownMemberResolved"] = present,
                    ["absentMemberIsNull"] = absent,
                },
            };
        }
        catch (Exception ex)
        {
            return Errored("bind.missingProbe", ex);
        }
    }

    // Seeds the `create` op required-field surface. DataTemplates are
    // ScriptableObjects, so construct via CreateInstance, never raw allocation:
    // a raw `new`/il2cpp_object_new yields a malformed ScriptableObject that
    // Unity rejects and later crashes on. CreateInstance is also the correct
    // path for the eventual create op.
    private static GateCheck CheckBlankSkillTemplate()
    {
        try
        {
            var so = ScriptableObject.CreateInstance(Il2CppType.Of<SkillTemplate>());
            var template = so != null ? so.TryCast<SkillTemplate>() : null;
            if (template == null)
            {
                return new GateCheck
                {
                    Name = "create.blankSkillTemplate",
                    Status = Fail,
                    Detail = "ScriptableObject.CreateInstance returned null or the wrong type",
                };
            }

            string id = null;
            try { id = template.GetID(); } catch { /* GetID may require population */ }

            return new GateCheck
            {
                Name = "create.blankSkillTemplate",
                Status = Pass,
                Detail = "blank SkillTemplate created via ScriptableObject.CreateInstance; field-surface enumeration is the follow-up",
                Evidence =
                {
                    ["pointerNonZero"] = template.Pointer != IntPtr.Zero,
                    ["idBeforePopulation"] = id ?? "<null>",
                },
            };
        }
        catch (Exception ex)
        {
            return Errored("create.blankSkillTemplate", ex);
        }
    }

    // Live: confirm the game's own dispatch reaches an injected handler. Isolate
    // the skill to only our handler so the fan-out runs nothing else, call the
    // game's Skill.OnTurnStart, then RunWithIsolatedHandler restores the originals.
    private static GateCheck CheckDispatchToInjected(bool live)
    {
        if (!live)
            return new GateCheck { Name = "dispatch.gameFansToInjected", Status = Skipped, Detail = "structural phase" };

        try
        {
            if (!TryGetActiveActor(out var actor))
                return new GateCheck { Name = "dispatch.gameFansToInjected", Status = Skipped, Detail = "no active actor" };

            var skills = actor.GetSkills()?.QueryActives();
            if (skills == null || skills.Count == 0)
                return new GateCheck { Name = "dispatch.gameFansToInjected", Status = Skipped, Detail = "no active skills" };

            var skill = skills[0];
            var originalCount = skill.m_EventHandlers != null ? (int)skill.m_EventHandlers.Length : 0;

            var handler = new GateEventHandler();
            RunWithIsolatedHandler(skill, handler, () => skill.OnTurnStart());
            var fired = handler.TurnStartFired;

            return new GateCheck
            {
                Name = "dispatch.gameFansToInjected",
                Status = fired ? Pass : Fail,
                Detail = "isolated the skill to only the injected handler, called the game's Skill.OnTurnStart fan-out, restored the originals",
                Evidence =
                {
                    ["originalHandlerCount"] = originalCount,
                    ["overrideFired"] = fired,
                },
            };
        }
        catch (Exception ex)
        {
            return Errored("dispatch.gameFansToInjected", ex);
        }
    }

    // Live: drive a lethal DamageInfo through the target's own
    // Entity.OnDamageReceived with the clamp handler attached, and confirm the
    // game honoured the override's mutation (target survives at the clamped
    // value rather than dying). This is the Voymastina mechanism end to end, and
    // the same path IgnoreDamage rides.
    private static GateCheck CheckDamageClampHonoured(bool live)
    {
        if (!live)
            return new GateCheck { Name = "damage.clampHonoured", Status = Skipped, Detail = "structural phase" };

        if (!IsDamageEnabled())
            return new GateCheck { Name = "damage.clampHonoured", Status = Skipped, Detail = "opt-in: drives a self-hit through the real damage pipeline. Create jiangyu-gate-damage.flag to enable." };

        try
        {
            if (!TryGetActiveActor(out var target))
                return new GateCheck { Name = "damage.clampHonoured", Status = Skipped, Detail = "no active actor" };

            var skills = target.GetSkills()?.QueryActives();
            if (skills == null || skills.Count == 0)
                return new GateCheck { Name = "damage.clampHonoured", Status = Skipped, Detail = "no active skills" };

            var skill = skills[0];
            var hpBefore = target.GetHitpoints();
            if (hpBefore <= 1)
                return new GateCheck { Name = "damage.clampHonoured", Status = Skipped, Detail = $"target HP too low to test ({hpBefore})" };

            var handler = new GateEventHandler { OwnerHpHint = hpBefore };
            var di = new DamageInfo { Damage = hpBefore };   // exactly lethal
            var props = target.GetCurrentProperties();
            DamageInfo result = null;
            RunWithIsolatedHandler(skill, handler, () => result = target.OnDamageReceived(target, skill, di, props));

            var hpAfter = target.GetHitpoints();
            var survived = hpAfter > 0;
            var honoured = handler.BeforeDamageCalls > 0 && handler.ClampApplied && survived;

            return new GateCheck
            {
                Name = "damage.clampHonoured",
                Status = honoured ? Pass : Fail,
                Detail = "drove a lethal DamageInfo through Entity.OnDamageReceived with the clamp handler attached; " +
                         "honoured means the override saw it, clamped, and the target survived",
                Evidence =
                {
                    ["hpBefore"] = hpBefore,
                    ["hpAfter"] = hpAfter,
                    ["survived"] = survived,
                    ["beforeDamageCalls"] = handler.BeforeDamageCalls,
                    ["clampApplied"] = handler.ClampApplied,
                    ["clampedDamageTo"] = handler.LastClampedDamage,
                    ["usedEntityHp"] = handler.UsedEntityHp,
                    ["resultDamage"] = result != null ? result.Damage : -1,
                },
            };
        }
        catch (Exception ex)
        {
            return Errored("damage.clampHonoured", ex);
        }
    }

    // The Odin question, scoped to what actually matters. Two probes, no mission
    // needed. (1) Polymorphic type-binder round-trip: BindToName then BindToType
    // on the injected type. If BindToType returns null, Odin would drop our
    // element on deserialise, which is the real polymorphic-survival failure.
    // (2) In-memory slot retention in an Odin-typed List<SkillEventHandlerTemplate>.
    //
    // Scope note in the detail: under runtime re-injection the binary SaveState
    // stores templates by m_ID and re-resolves them from the live registry each
    // session, so field values are rebuilt by code and never transit Odin or the
    // save. Odin asset serialisation only bites the bake-into-resources.assets
    // distribution. Full save/load registry re-resolution is a separate launch
    // sub-step against the strategy layer.
    private static GateCheck CheckOdinSerialisationSurvival()
    {
        var check = new GateCheck
        {
            Name = "odin.serialisationSurvival",
            Detail = "Odin polymorphic type-binder round-trip for the injected type, plus in-memory slot retention. " +
                     "Under runtime re-injection the binary SaveState stores templates by m_ID and re-resolves from the " +
                     "live registry, so field values never transit Odin or the save; Odin asset serialisation only matters " +
                     "for a bake-into-assets distribution.",
        };

        var binderOk = false;
        var slotOk = false;

        try
        {
            var injectedType = Il2CppType.Of<GateHandlerTemplate>();
            var binder = new DefaultSerializationBinder();
            var name = binder.BindToName(injectedType);
            var resolved = !string.IsNullOrEmpty(name) ? binder.BindToType(name) : null;
            binderOk = resolved != null;
            check.Evidence["binderName"] = name ?? "<null>";
            check.Evidence["binderResolvedType"] = resolved != null ? resolved.FullName : "<null>";
            check.Evidence["binderRoundTrips"] = binderOk;
        }
        catch (Exception ex)
        {
            check.Evidence["binderError"] = $"{ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            var template = CreateGateTemplate();
            if (template == null)
            {
                check.Evidence["slotError"] = "could not construct GateHandlerTemplate via ScriptableObject.CreateInstance";
            }
            else
            {
                template.InvulnTurns = 7;
                var list = new Il2CppSystem.Collections.Generic.List<SkillEventHandlerTemplate>();
                list.Add(template);
                var readBack = list[0]?.TryCast<GateHandlerTemplate>();
                slotOk = readBack != null && readBack.InvulnTurns == 7;
                check.Evidence["slotReadBackIsGateType"] = readBack != null;
                check.Evidence["slotInvulnTurns"] = readBack != null ? readBack.InvulnTurns : -1;
                check.Evidence["slotRetained"] = slotOk;
            }
        }
        catch (Exception ex)
        {
            check.Evidence["slotError"] = $"{ex.GetType().Name}: {ex.Message}";
        }

        // Status gates on in-memory slot retention, which is all the runtime
        // reinject path needs. binderRoundTrips is informational: a null resolve
        // means a bake-into-assets distribution would drop the element on
        // deserialise (and need a custom binder), but runtime reinject never
        // round-trips through Odin.
        check.Status = slotOk ? Pass : Fail;
        return check;
    }

    // Temporarily swap a skill's handler array for one holding only the given
    // handler, run body, then always restore the originals. The game's fan-out
    // reaches only our handler and the skill is left untouched afterwards.
    // (Skill.AddEventHandler is an indexed write into a fixed-size array, not an
    // append, so a full swap is the safe way to attach a runtime handler.)
    private static void RunWithIsolatedHandler(Skill skill, SkillEventHandler handler, Action body)
    {
        var original = skill.m_EventHandlers;
        skill.m_EventHandlers = new Il2CppReferenceArray<SkillEventHandler>(new SkillEventHandler[] { handler });
        try
        {
            body();
        }
        finally
        {
            skill.m_EventHandlers = original;
        }
    }

    private static GateHandlerTemplate CreateGateTemplate()
    {
        try
        {
            var so = ScriptableObject.CreateInstance(Il2CppType.Of<GateHandlerTemplate>());
            return so != null ? so.TryCast<GateHandlerTemplate>() : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetActiveActor(out Actor actor)
    {
        actor = null;
        try
        {
            var tm = TacticalManager.Get();
            actor = tm != null ? tm.m_ActiveActor : null;
            return actor != null;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeGameVersion()
    {
        try { return Application.version; }
        catch { return "<unknown>"; }
    }

    private static GateCheck Errored(string name, Exception ex)
        => new() { Name = name, Status = Error, Detail = $"{ex.GetType().Name}: {ex.Message}" };

    private static bool IsDamageEnabled()
    {
        try
        {
            return File.Exists(System.IO.Path.Combine(MelonEnvironment.UserDataDirectory, DamageFlagFileName));
        }
        catch
        {
            return false;
        }
    }

    // Append and log immediately, so the result survives a later native crash.
    private static void Emit(GateReport report, GateCheck check, MelonLogger.Instance log)
    {
        report.Checks.Add(check);
        log.Msg($"[gate] {check.Name}: {check.Status.ToUpperInvariant()}  {check.Detail}");
    }

    private static void Write(GateReport report, string sceneTag, MelonLogger.Instance log)
    {
        var passes = 0;
        var fails = 0;
        foreach (var c in report.Checks)
        {
            if (c.Status == Pass) passes++;
            else if (c.Status == Fail || c.Status == Error) fails++;
        }

        try
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd-HH-mm-ss");
            var safeTag = InspectionSink.SanitiseForFileName(sceneTag);
            var path = System.IO.Path.Combine(InspectionSink.GetOutputDirectory(), $"{timestamp}-gate-{safeTag}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, InspectionSink.JsonOptions));
            log.Msg($"[gate] {passes} passed, {fails} failed/errored, {report.Checks.Count} total. Report: {path}");
        }
        catch (Exception ex)
        {
            log.Error($"[gate] report write failed: {ex}");
        }
    }

    private sealed class GateReport
    {
        public DateTimeOffset Timestamp { get; set; }
        public string SceneTag { get; set; }
        public bool Live { get; set; }
        public string SdkLoaderVersion { get; set; }
        public string GameVersion { get; set; }
        public List<GateCheck> Checks { get; } = new();
    }

    private sealed class GateCheck
    {
        public string Name { get; set; }
        public string Status { get; set; }
        public string Detail { get; set; }
        public Dictionary<string, object> Evidence { get; } = new(StringComparer.Ordinal);
    }
}
