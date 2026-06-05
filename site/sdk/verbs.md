#  Verbs

[Hooks](/sdk/#hooks) tell you a moment happened. [Template types](./template-types) change what the game's data does. **Game verbs** are the third surface: they let your code *read* and *command* the live game directly. Spawn a unit, query a path, list the actors on the field, ask a skill's hit chance.

The verbs live in the `Jiangyu.Game` namespace and are plain static calls, like `Log`. They need no `Context`, so any code the loader runs can use them:

```csharp
using Jiangyu.Game.Tactical;

var actor = Mission.ActiveActor;
if (actor != null)
    Combat.CanSee(actor.GetTile(), someTile);
```

They return real game types (`Actor`, `Tile`, `Skill`), so you call straight through to the game's own API from there. The full list is in the [verb reference](/reference/verbs). This page is the tour.

## What a verb is (and is not)

A verb is a thin, discoverable wrapper over a game call. It adds a name you can find and the ergonomics around an awkward call (an out-parameter turned into a return, an IL2CPP collection copied into a real `List`, a pathfinding request and release bracketed for you). It does **not** add safety: called where the game does not expect it, a verb faults exactly as the raw call would. That is yours to avoid, not the verb's to absorb.

That is milder than it sounds, and deliberate. A misused verb throws where you got it wrong and lands in the log, the session keeps running, a loud, fixable error rather than a dead game or a silent wrong result. A guard would only trade that away, turning the logged fault into a quiet no-op you would not notice until a player did, and an honest one is not ours to write anyway: "safe" means knowing the game's rules, and a rule we invent is one a patch quietly turns into a lie. Thin also stays a primitive you can build on, a `SafeSpawn` with exactly the checks your mod wants is three lines over `Units.Spawn`, where a guard baked in for you is one mod's idea of "safe" forced on the next, which may well *want* to spawn onto an "occupied" tile.

So check `Mission.InMission` before the tactical verbs when you might be outside a mission. Only your code knows whether now is a sensible moment to call one. The single mistake that can corrupt game state, mutating while the AI is mid-decision, the **JIA009** analyzer flags as you type (see [Mutating with intent](#mutating-with-intent)).

So the tactical verbs need a live tactical mission. `Mission.Map` outside one throws, the same as reaching for the manager yourself. Check `Mission.InMission` first when you might be outside one. Context is yours to get right because only your code knows it: a verb cannot tell whether now is a sensible moment to call it, but your code can.

## Where you can call them

Anywhere the loader dispatches into your code:

- inside a [hook](/sdk/#hooks) handler,
- in a `JiangyuSystem` lifecycle method (`OnInit`, `OnSceneLoaded`, ...),
- inside a [template type](./template-types) override the game invokes (a condition's `IsTrue`, a handler's `OnTurnStart`, an effect's `Create`).

A pure-data mod (templates only, no `code/`) cannot use verbs, because there is no code to call them. The moment you want one, you have a [code project](/sdk/#the-code-project).

## The families

The verbs are grouped by the part of the game they touch. The tactical layer has `Mission` (the live mission and the actors on the field), `Units` (spawn, despawn, move), `Combat` (hit chance, line of sight, damage), `Pathing`, and `Tiles`. The strategy layer has `Campaign`, `Leaders`, `Market`, `Operations`, and the rest of the campaign meta-game. The [verb reference](/reference/verbs) is the full list. A few examples give the feel.

A read that flattens the field into a list you can iterate:

```csharp
foreach (var enemy in Mission.Actors(FactionType.Pirates))
    Log.Info($"{enemy.GetName()} at {enemy.GetTile().GetX()},{enemy.GetTile().GetZ()}");
```

A query that goes straight to the game's own answer:

```csharp
float chance = Combat.HitChance(skill, actor.GetTile(), target.GetTile());
```

A mutation that returns whatever the game returns:

```csharp
var unit = Units.Spawn(template, FactionType.Player, tile);   // the spawned Actor, or null if the game refuses
```

`Units.Spawn` is a thin pass to `TacticalManager.TrySpawnUnit`. It spawns for any faction and validates nothing, so an occupied tile or a wall is yours to avoid, not the verb's to catch. That thinness is why *where* and *when* you call a mutation matters.

## Mutating with intent

A read you get wrong is cheap to spot. A mutation (`Spawn`, `Despawn`, `Move`, and the strategy-layer mutators) is not, because *where* and *when* you call it matters. Three habits, the first caught for you at author time.

### Call mutations on a committed action, not a prediction

A condition's `IsTrue` runs while the AI is *evaluating* its options. Spawning or killing there corrupts the decision the game is mid-way through making. Trigger mutations from a point that represents something actually happening: a skill being used, a turn starting, a unit dying. A mutating verb (one marked `[MutatingVerb]`) called from a predicate or polling override is flagged by the **JIA009** analyzer as you type.

### Guard for repetition

A polling override (`OnUpdate`, `OnTurnStart`) fires every frame or every turn, and your injected type is re-applied every time the game loads. So a `Units.Spawn` in `OnTurnStart` spawns again each turn and again on reload. Prefer expressing a one-off as an effect on the game's own trigger (which fires once), or guard it with [per-save state](/sdk/#state):

```csharp
var state = Context.State.Get<MyState>();
if (!state.Reinforced)
{
    Units.Spawn(template, faction, tile);
    state.Reinforced = true;
}
```

### Treat hook payloads as perishable

A hook hands you a game object that may be gone by the next frame. If you stash one and act on it later, guard with `IsAlive` first:

```csharp
Context.Hooks.Subscribe<EntityDiedContext>(ctx =>
{
    var victim = ctx.Victim as Entity;
    Context.Coroutines.Start(Later(victim));
});

System.Collections.IEnumerator Later(Entity victim)
{
    yield return new UnityEngine.WaitForSeconds(2);
    if (victim.IsAlive())          // the object may be gone by now
        Log.Info($"{victim.GetName()} is still around");
}
```

`IsAlive` reports an object the runtime has seen collected, but cannot prove one freed in some other way is dead. For a reference held across many frames, prefer re-resolving it through a live lookup (`Mission.Actors()`) rather than trusting the old one.

### The analyzer has your back

You do not have to remember all of this. The Jiangyu analyzer flags the mistakes as you type:

- **JIA007** if you call `base.Something()` from a [template type](./template-types) override (it crashes the game).
- **JIA008** if you `Subscribe` to a hook from `OnSceneLoaded` or `OnUpdate` (it stacks duplicate handlers, [subscribe in `OnInit`](/sdk/#systems)).
- **JIA009** if you put a mutating verb in a predicate or polling override.

## A worked example

Verbs come into their own when a [template type](./template-types) drives them. The template says *when* in KDL, the type's code says *what* with verbs, and the game's own pipeline fires it.

```kdl
// templates/skills.kdl, references your type by name
patch "skill.reinforce" {
    append "EventHandlers" type="MyMod:SpawnSquad" { count 2 }
}
```

```csharp
// code/, the type the template names
using Jiangyu.Sdk;
using Jiangyu.Game.Tactical;
using Il2CppMenace.Tactical;

[JiangyuType("SpawnSquad")]
public sealed partial class SpawnSquad : SkillEventHandlerTemplate
{
    public int count = 1;
    public override SkillEventHandler Create() => new SpawnSquadHandler { Count = count };
}

public sealed class SpawnSquadHandler : SkillEventHandler
{
    public int Count;

    // Fired after the skill is used: a committed action, so the mutation is sound.
    public override void OnAfterUse()
    {
        var caster = Mission.ActiveActor;
        for (var i = 0; i < Count && caster != null; i++)
        {
            var spot = Mission.TileAt(caster.GetTile().GetX() + i + 1, caster.GetTile().GetZ());
            Units.Spawn(caster.GetTemplate(), caster.GetFaction(), spot);
        }
    }
}
```

The modder using this mod writes only the KDL. The skill carries the behaviour, and the game spawns the squad when the skill fires.
