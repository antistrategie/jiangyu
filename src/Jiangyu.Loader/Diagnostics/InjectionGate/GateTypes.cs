using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Injection;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;
using Il2CppMenace.Tactical.Skills.Effects;

namespace Jiangyu.Loader.Diagnostics.InjectionGate;

// Code-defined IL2CPP subtypes injected for the Phase 0 injection go/no-go.
// Three roots cover the breadth test: a runtime SkillEventHandler (dispatch and
// the lethal-veto shape), a TacticalCondition (decision override), and a
// SkillEventHandlerTemplate (the Odin-serialised data-side factory). This is
// not production code. It is the re-runnable evidence path that the verified
// promotion rules require before any of this is trusted.
//
// Signatures are taken from the live Assembly-CSharp proxy:
//   SkillEventHandler.OnTurnStart()
//   SkillEventHandler.OnBeforeDamageReceived(Skill, Entity, DamageInfo, EntityProperties)
//   TacticalCondition.IsTrue(Entity, Skill)
//   SkillEventHandlerTemplate.Create() : SkillEventHandler

/// <summary>Root 1: runtime handler. Proves dispatch and the damage-clamp shape.</summary>
public sealed class GateEventHandler : SkillEventHandler
{
    public GateEventHandler(IntPtr ptr) : base(ptr) { }

    public GateEventHandler()
        : base(ClassInjector.DerivedConstructorPointer<GateEventHandler>())
        => ClassInjector.DerivedConstructorBody(this);

    public bool TurnStartFired;
    public int BeforeDamageCalls;
    public bool ClampApplied;
    public int LastClampedDamage = -1;
    public bool UsedEntityHp;

    // Set by the inspector before a driven hit so the clamp triggers
    // deterministically even if GetEntity() is not wired on a detached handler.
    public int OwnerHpHint = -1;

    // Overrides deliberately do not call base. For an injected IL2CPP type a
    // base-virtual call can re-dispatch to the override (recursion to a native
    // crash). The base bodies are no-ops, so nothing is lost.
    public override void OnTurnStart()
    {
        TurnStartFired = true;
    }

    // The Voymastina veto shape: clamp lethal damage to leave the owner alive.
    public override void OnBeforeDamageReceived(Skill _skill, Entity _attacker, DamageInfo _damageInfo, EntityProperties _properties)
    {
        BeforeDamageCalls++;
        if (_damageInfo != null)
        {
            var hp = 0;
            try
            {
                var owner = GetEntity();
                if (owner != null)
                {
                    hp = owner.GetHitpoints();
                    UsedEntityHp = hp > 0;
                }
            }
            catch { /* detached handler: fall back to the hint */ }

            if (hp <= 0)
                hp = OwnerHpHint;

            var lethal = _damageInfo.IsTargetDestroyed || (hp > 0 && _damageInfo.Damage >= hp);
            if (lethal)
            {
                _damageInfo.Damage = hp > 1 ? hp - 1 : 0;
                LastClampedDamage = _damageInfo.Damage;
                ClampApplied = true;
            }
        }
    }
}

/// <summary>Root 2: decision condition. Proves the IsTrue override surface.</summary>
public sealed class GateCondition : TacticalCondition
{
    public GateCondition(IntPtr ptr) : base(ptr) { }

    public GateCondition()
        : base(ClassInjector.DerivedConstructorPointer<GateCondition>())
        => ClassInjector.DerivedConstructorBody(this);

    public bool IsTrueCalled;
    public bool Result = true;

    public override bool IsTrue(Entity _checkTarget, Skill _skill)
    {
        IsTrueCalled = true;
        return Result;
    }
}

/// <summary>
/// Root 3: the Odin-serialised data-side factory. Whether <see cref="InvulnTurns"/>
/// (a managed field added to an injected SerializedScriptableObject subtype)
/// survives an Odin save/load round-trip is the highest-risk gate question. Even
/// registering and constructing a ScriptableObject-rooted injected type is not a
/// given, so the inspector guards both and records the outcome rather than
/// assuming success.
/// </summary>
public sealed class GateHandlerTemplate : SkillEventHandlerTemplate
{
    public GateHandlerTemplate(IntPtr ptr) : base(ptr) { }

    public GateHandlerTemplate()
        : base(ClassInjector.DerivedConstructorPointer<GateHandlerTemplate>())
        => ClassInjector.DerivedConstructorBody(this);

    public int InvulnTurns = 3;

    public bool ApplyPropsCalled;
    public bool IsUsableCalled;

    public override SkillEventHandler Create() => new GateEventHandler();

    // Passive-effect surface: the game calls this on a skill/perk's templates to
    // fold static modifiers into an entity's computed properties. A modder
    // overriding it ships a passive stat effect with no runtime handler at all.
    public override void ApplyToEntityProperties(EntityProperties _properties)
    {
        ApplyPropsCalled = true;
    }

    // Usability gate: the game asks the template whether the effect may apply.
    public override bool IsUsable()
    {
        IsUsableCalled = true;
        return true;
    }
}

/// <summary>
/// Root 4: a custom dynamic value source. Value providers (distance, missing
/// health, and so on) feed scaled skill values; they implement the IValueProvider
/// IL2CPP interface on a plain object rather than deriving from a shared base.
/// Injecting one tests interface-implementer registration (RegisterTypeOptions
/// with the interface), a different mechanism from the base-class override roots:
/// the managed method matches the interface method by name and signature, and the
/// injector wires it into the interface vtable slot.
/// </summary>
public sealed class GateValueProvider : Il2CppSystem.Object
{
    public GateValueProvider(IntPtr ptr) : base(ptr) { }

    public GateValueProvider()
        : base(ClassInjector.DerivedConstructorPointer<GateValueProvider>())
        => ClassInjector.DerivedConstructorBody(this);

    public const float Sentinel = 1337f;
    public bool GetValueCalled;

    public float GetValue(Entity _user, Entity _target, Skill _skill)
    {
        GetValueCalled = true;
        return Sentinel;
    }
}

/// <summary>
/// Registers the three roots exactly once per process. Re-registration throws in
/// Il2CppInterop, so the attempt is latched. Each root is registered
/// independently so one failing root (the likely candidate is the
/// ScriptableObject-rooted template) does not mask the others.
/// </summary>
internal static class GateTypeRegistrar
{
    private static bool _attempted;
    private static bool _allOk;
    private static string _detail = "";
    private static readonly Dictionary<string, bool> _perRoot = new(StringComparer.Ordinal);

    public static bool TryRegister(out string detail, out IReadOnlyDictionary<string, bool> perRoot)
    {
        if (!_attempted)
        {
            _attempted = true;
            var parts = new List<string>();
            var ok = true;
            ok &= One<GateEventHandler>("SkillEventHandler", parts);
            ok &= One<GateCondition>("TacticalCondition", parts);
            ok &= One<GateHandlerTemplate>("SkillEventHandlerTemplate", parts);
            ok &= OneWithInterface<GateValueProvider>("IValueProvider", typeof(IValueProvider), parts);
            _allOk = ok;
            _detail = string.Join("; ", parts);
        }

        detail = _detail;
        perRoot = _perRoot;
        return _allOk;
    }

    private static bool One<T>(string root, List<string> parts) where T : class
    {
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<T>();
            _perRoot[root] = true;
            parts.Add($"{root}=registered");
            return true;
        }
        catch (Exception ex)
        {
            _perRoot[root] = false;
            parts.Add($"{root}=FAILED:{ex.GetType().Name}:{ex.Message}");
            return false;
        }
    }

    // Register a type that satisfies an IL2CPP interface. The managed method is
    // matched into the interface's vtable slot via RegisterTypeOptions.Interfaces.
    private static bool OneWithInterface<T>(string root, Type iface, List<string> parts) where T : class
    {
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<T>(new RegisterTypeOptions
            {
                Interfaces = new Il2CppInterfaceCollection(new[] { iface }),
            });
            _perRoot[root] = true;
            parts.Add($"{root}=registered");
            return true;
        }
        catch (Exception ex)
        {
            _perRoot[root] = false;
            parts.Add($"{root}=FAILED:{ex.GetType().Name}:{ex.Message}");
            return false;
        }
    }
}
