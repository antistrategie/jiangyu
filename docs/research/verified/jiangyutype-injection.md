# JiangyuType Injection And Dispatch

Status: **verified** (Jiangyu live-game runs, game `v0.7.4+18448`, 2026-06-02).

Reproduced by the re-runnable `InjectionGate` diagnostic
(`src/Jiangyu.Loader/Diagnostics/InjectionGate/`, gated by a `jiangyu-gate.flag`
file in `<UserData>`). Draws from
[`../investigations/2026-06-02-jiangyutype-injection-and-dispatch-spike.md`](../investigations/2026-06-02-jiangyutype-injection-and-dispatch-spike.md).

## Contract

A managed, code-defined type can be injected into the game's IL2CPP type system
as a polymorphic subtype of a game base, and the game's own dispatch calls its
overrides. This is the mechanism the `[JiangyuType]` SDK design rests on.

Verified on the live game:

- **Registration.** `ClassInjector.RegisterTypeInIl2Cpp<T>()` succeeds for three
  distinct roots: a runtime `SkillEventHandler`, a `TacticalCondition`, and a
  `SkillEventHandlerTemplate` (a `SerializedScriptableObject`).
- **Runtime assignability.** Each injected instance is assignable to its game
  base (`TryCast<Base>` non-null, non-zero pointer).
- **Game dispatch reaches the override.** With an injected
  `SkillEventHandler` placed in a live skill's `m_EventHandlers`, the game's own
  `Skill.OnTurnStart` fan-out invokes the injected override (`overrideFired`
  true). The override surface matches the live signatures, including
  `OnBeforeDamageReceived(Skill, Entity, DamageInfo, EntityProperties)`,
  `TacticalCondition.IsTrue(Entity, Skill)`, and
  `SkillEventHandlerTemplate.Create()`.
- **Blank construction.** A `SkillTemplate` constructs via
  `ScriptableObject.CreateInstance` with an empty `m_ID`.

## Required practices (each established by a live failure)

- **Never call `base.*` from an injected-type override.** A base-virtual call
  from an injected type can re-dispatch to the override, recursing to a native
  stack-overflow crash with no managed trace. Overrides must be self-contained.
- **Construct ScriptableObject-rooted types via `ScriptableObject.CreateInstance`,
  never raw allocation.** A raw `new` / `il2cpp_object_new` yields a malformed
  ScriptableObject that Unity rejects and later crashes on.
- **`Skill.AddEventHandler(handler, index)` is an indexed write into a
  fixed-size `Il2CppReferenceArray`, not an append** (index 0 clobbers, index ==
  length throws). To attach a runtime handler, swap in a new array. The natural
  SDK path avoids this: handlers enter via the template's `EventHandlers` and
  `Create()` at skill construction.

## Odin and persistence

- **Odin's type binder cannot resolve an injected type by name.**
  `DefaultSerializationBinder.BindToName` yields `...Type, InjectedMonoTypes`
  and `BindToType` of that returns null, so Odin would drop an injected
  polymorphic element on deserialise. This constrains a
  bake-into-`resources.assets` distribution (it would need a custom binder). It
  does not affect runtime re-injection, which never round-trips through Odin: the
  binary `SaveState` stores templates by `m_ID` and re-resolves them from the
  live registry each session, so a code-defined template's field values are
  rebuilt by the loader on every launch.
- **In-memory slot retention holds.** An injected `SkillEventHandlerTemplate`
  stored in an Odin-typed `List<SkillEventHandlerTemplate>` reads back as its
  injected type with its managed field intact.

## Not yet proven here

The explicit damage-clamp behaviour (an `OnBeforeDamageReceived` override
reducing lethal damage so the target survives) is not run by the diagnostic by
default (it drives a self-hit, opt-in behind `jiangyu-gate-damage.flag`). It is
strongly implied by the verified dispatch plus the stock `IgnoreDamage` handler,
which absorbs damage through this same path, and will be proven in context when
the perk is built.
