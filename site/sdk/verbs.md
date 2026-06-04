# Game verbs

Hooks tell you a moment happened. [Template types](./template-types) change what the game's data does. **Game verbs** are the third surface: they let your code *read* and *command* the live game directly. Spawn a unit, query a path, list the actors on the field, ask a skill's hit chance.

The verbs live in the `Jiangyu.Game` namespace and are plain static calls, like `Log`. They need no `Context`, so any code the loader runs can use them:

```csharp
using Jiangyu.Game;

var leader = Tactical.ActiveActor;
if (leader != null)
    Combat.CanSee(leader.GetTile(), someTile);
```

They return real game types (`Actor`, `Tile`, `Skill`), so you call straight through to the game's own API from there.

## Where you can call them

Anywhere the loader dispatches into your code:

- inside a [hook](./hooks) handler,
- in a `JiangyuMod` lifecycle method (`OnInit`, `OnSceneLoaded`, ...),
- inside a [template type](./template-types) override the game invokes (a condition's `IsTrue`, a handler's `OnTurnStart`, an effect's `Create`).

A pure-data mod (templates only, no `code/`) cannot use verbs, because there is no code to call them. The moment you want one, you have a [code project](./template-types#the-code-project).

The tactical verbs need a tactical mission. Outside one they return null or empty rather than throwing, so a verb called from the strategy layer or a mistimed handler quietly does nothing instead of crashing.

## Tactical reads

`Tactical` is the entry point to the live mission. Every member is safe to call at any time.

| Member | Returns |
| --- | --- |
| `Tactical.InMission` | whether a tactical mission is running |
| `Tactical.ActiveActor` | the actor whose turn it is, or null |
| `Tactical.Map` | the live map, or null outside a mission |
| `Tactical.Round` | the round number, or -1 outside a mission |
| `Tactical.TileAt(x, z)` | the tile at a coordinate, or null |
| `Tactical.Actors(faction?)` | every actor on the field, optionally one faction |

```csharp
foreach (var enemy in Tactical.Actors(FactionType.Pirates))
    Log.Info($"{enemy.GetName()} at {enemy.GetTile().GetX()},{enemy.GetTile().GetZ()}");
```

## Units

`Units.Spawn` puts a unit on the field. It returns a `SpawnOutcome` rather than a bare bool, because a spawn can be refused for several reasons:

```csharp
var outcome = Units.Spawn(template, FactionType.Player, tile);
if (outcome.Ok)
    Log.Info($"spawned at {tile.GetX()},{tile.GetZ()}");
else
    Log.Warn($"no spawn: {outcome.Reason}");
```

You can spawn for any faction (an enemy unit is as valid as a friendly one). `Spawn` refuses an occupied or blocked tile, but those are Jiangyu safeties, not game rules: the game validates neither (it will happily stack units or spawn into a wall), so if you genuinely want that, call `TacticalManager.TrySpawnUnit` directly.

`Units.Despawn(actor)` removes a unit: it leaves the faction roster, frees its tile, and drops to 0 HP. It returns false for a unit that is already gone, and uses the liveness check below so it never acts on a dead reference.

```csharp
Units.Despawn(outcome.Unit);
```

`Spawn` and `Despawn` change the battlefield, so they are guarded. See [Mutating safely](#mutating-safely).

## Pathing and combat

`Pathing.To` returns the path a mover would take as world-space waypoints, or an empty list when there is no path:

```csharp
var waypoints = Pathing.To(actor, actor.GetTile(), destination);
```

`Combat` answers read-only questions, safe to call from anywhere including a predicate:

```csharp
float chance = Combat.HitChance(skill, actor.GetTile(), target.GetTile());
bool   seen  = Combat.CanSee(actor.GetTile(), target.GetTile());
```

## Mutating safely

Reads cost you nothing to get wrong: outside a mission they return null or empty. Mutations (`Spawn`, `Despawn`) need more care, because *where* and *when* you call them matters. Three rules, each enforced for you.

### Call mutations on a committed action, not a prediction

A condition's `IsTrue` runs while the AI is *evaluating* its options. Spawning or killing there corrupts the decision the game is mid-way through making, so a mutating verb called during evaluation is **refused at runtime** and logged. Trigger mutations from a point that represents something actually happening: a skill being used, a turn starting, a unit dying.

### Guard for repetition

A polling override (`OnUpdate`, `OnTurnStart`) fires every frame or every turn, and your injected type is re-applied every time the game loads. So a `Units.Spawn` in `OnTurnStart` spawns again each turn and again on reload. Prefer expressing a one-off as an effect on the game's own trigger (which fires once), or guard it with [per-save state](./hooks#state):

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

`IsAlive` reports an object the runtime has seen collected, but cannot prove one freed in some other way is dead. For a reference held across many frames, prefer re-resolving it through a live lookup (`Tactical.Actors()`) rather than trusting the old one.

### The compiler has your back

You do not have to remember all of this. The Jiangyu analyzer flags the mistakes as you type:

- **JIA007** if you call `base.Something()` from a [template type](./template-types) override (it crashes the game).
- **JIA008** if you `Subscribe` to a hook from `OnSceneLoaded` or `OnUpdate` (it stacks duplicate handlers, [subscribe in `OnInit`](./hooks#the-entry-point)).
- **JIA009** if you put a mutating verb in a predicate or polling override.

## A worked example

Verbs come into their own when a [template type](./template-types) drives them. The template says *when* in KDL, the type's code says *what* with verbs, and the game's own pipeline fires it.

```kdl
// templates/skills.kdl — references your type by name
patch "skill.reinforce" {
    append "EventHandlers" type="MyMod:SpawnSquad" { count 2 }
}
```

```csharp
// code/ — the type the template names
using Jiangyu.Sdk;
using Jiangyu.Game;
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

    // Fired after the skill is used: a committed action, so the mutation is safe.
    public override void OnAfterUse()
    {
        var caster = Tactical.ActiveActor;
        for (var i = 0; i < Count && caster != null; i++)
        {
            var spot = Tactical.TileAt(caster.GetTile().GetX() + i + 1, caster.GetTile().GetZ());
            Units.Spawn(caster.GetTemplate(), caster.GetFaction(), spot);
        }
    }
}
```

The modder using this mod writes only the KDL. The skill carries the behaviour, and the game spawns the squad when the skill fires.
