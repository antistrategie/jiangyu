# Hooks and the mod API

A **behaviour mod** reacts to game moments and runs logic, rather than (or as well as) adding [template types](./template-types). It has one `JiangyuMod` subclass as its entry point, through which it reaches the loader's runtime services: hooks, per-save state, the mod's own assets, coroutines, and method patches.

Set up `code/` and discover the game's types exactly as for [template types](./template-types#the-code-project). If your mod only adds template types, you do not need any of this page, there is no entry point to write.

## The entry point

The loader discovers one `JiangyuMod` per mod, instantiates it, binds its `Context`, and drives the lifecycle. Every method is a no-op by default, so override only what you need.

```csharp
using Jiangyu.Sdk;

public sealed class MyMod : JiangyuMod
{
    public override void OnInit()
    {
        Log.Info("loaded");
        Context.Hooks.Subscribe<RoundStartedContext>(ctx =>
            Log.Info($"round {ctx.Round} started"));
    }
}
```

| Override | Called |
| --- | --- |
| `OnInit()` | Once, after the mod loads and its `Context` is bound. |
| `OnTemplatesApplied()` | Once, after every mod's template clones and patches have landed on the live templates. Read or further adjust the final merged template set here. |
| `OnSceneLoaded(buildIndex, sceneName)` | Each time a Unity scene finishes loading. |
| `OnUpdate()` | Every frame. Override only when you genuinely need per-frame work. |
| `OnUnload()` | On shutdown or hot reload. The loader stops your coroutines and removes your patches for you. |

## The mod API

`Context` is the mod's handle on the loader's services. A `[JiangyuType]` handler with no `Context` of its own reaches the same services through `ModContext.For(this)`.

| Member | What it gives you |
| --- | --- |
| `Log` | Per-mod logging (`Info`, `Warn`, `Error`, `Debug`), auto-tagged with your mod id. The static `Jiangyu.Sdk.Log` is the same logger and needs no `Context`, so most code just calls `Log.Info(...)`. `Debug` is dropped unless the dev opts in. |
| `Hooks` | Subscribe to global game moments. See [Hooks](#hooks). |
| `State` | Per-save-slot persistent state. See [State](#state). |
| `Assets` | Load the mod's own bundled assets by name. See [Assets](#assets). |
| `Coroutines` | Run multi-frame or timed logic. See [Coroutines](#coroutines). |
| `Patches` | Patch a game method no hook covers. The escape hatch. See [Patches](#patches). |
| `ModFolder` | Absolute path to the deployed `Mods/<ModId>` folder. |
| `ModId`, `Version` | The mod id and the version from `jiangyu.json`. |

## Hooks

Hooks are global, no-anchor moments: every kill, a round boundary, a leader hired, a save or load. Subscribe by the context type; the subscription lives for the mod's lifetime.

```csharp
Context.Hooks.Subscribe<EntityDiedContext>(ctx =>
{
    var victim = ctx.Victim as Entity;   // game-typed payloads are object, cast them
    Log.Info($"{victim?.GetName()} died");
});
```

Every moment is a context type. The [hook reference](#hook-reference) below lists all of them with their payloads, split into a tactical family (combat, turns, movement, skills) and a strategy family (factions, leaders, operations). Game-typed payloads are held as `object` to keep the SDK game-agnostic, so you cast them in your handler (the reference notes which game type each is). Primitive payloads, a round number or a count, are typed directly.

The bus is observer-only: it tells you a moment happened, it does not let you cancel it. To change what a skill or effect does, write a [template type](./template-types). To read or command the live game from a handler (spawn a unit, query a path), use [game verbs](./verbs). To intercept a method with no hook, use [Patches](#patches).

## State

`Context.State` persists mod-owned data across save and load, in a sidecar beside the save file and keyed by the save slot so it never leaks between saves.

```csharp
public sealed class MyState { public int TimesSpawned; }

var state = Context.State.Get<MyState>();
state.TimesSpawned++;   // mutate in place; written when the game saves
```

Most mod state should be game state, not this: a marker skill, stacks, a status with a duration. Reach for `State` only for genuinely out-of-band data the game save does not already hold.

## Assets

`Context.Assets` loads the mod's own bundled assets on demand, by name. Only this mod's bundles are visible, never another mod's, and a loaded asset is kept alive by the loader for the session.

```csharp
var icon = Context.Assets.Load<UnityEngine.Sprite>("my_icon");
```

`T` is a UnityEngine type and the name matches the asset's short name or its full path inside the bundle.

## Coroutines

Run logic across frames or over time with a normal C# iterator.

```csharp
Context.Coroutines.Start(Settle());

System.Collections.IEnumerator Settle()
{
    yield return null;                              // resume next frame
    yield return new UnityEngine.WaitForSeconds(1); // or wait a beat
    Log.Info("a second later");
}
```

Use it to act after a synchronous hook once the game state has settled, to poll for a condition no hook covers, or to sequence an effect over time. Routines are stopped for you when the mod unloads.

## Patches

When no hook covers the moment you need, patch the game method directly. Name it by its declaring type and method; your handler runs before (prefix) or after (postfix).

```csharp
Context.Patches.Postfix("Il2CppMenace.Tactical.Actor", "TakeDamage", info =>
{
    var actor = info.Instance as Actor;
    Log.Info($"{actor?.GetName()} took damage");
});
```

A prefix handler may set `info.Skip = true` to stop the original method running. This is the escape hatch: prefer a hook where one exists, and a template type to change an effect. Patches are tracked per mod and removed on unload.

## Hook reference

Every context below is a type you pass to `Context.Hooks.Subscribe<T>`. Payload fields without a noted primitive type are game objects held as `object`: cast them to the named game type (for example `ctx.Victim as Entity`). A `—` payload means the moment carries no data.

### Tactical

Fired off the mission's `TacticalManager`, live only while a tactical mission is loaded.

| Context | Payload |
| --- | --- |
| `RoundStartedContext` | `Round` (int) |
| `MissionStartedContext` | — |
| `MissionFinishedContext` | — |
| `ObjectiveStateChangedContext` | `Objective`, `OldState`, `NewState` (ObjectiveState) |
| `EntitySpawnedContext` | `Entity` |
| `TurnStartedContext` | `Actor` |
| `TurnEndedContext` | `Actor` |
| `ActiveActorChangedContext` | `Actor` |
| `ActorActedContext` | `Actor` |
| `PlayerTurnContext` | — |
| `AITurnContext` | `Faction` (int) |
| `EntityDiedContext` | `Victim`, `Killer` (Entity) |
| `DamageReceivedContext` | `Victim`, `Source` (Entity), `Skill`, `Damage` (DamageInfo) |
| `AttackMissedContext` | `Target`, `Attacker` (Entity), `Skill` |
| `AttackTileStartedContext` | `Attacker` (Actor), `Skill`, `Tile`, `DurationSeconds` (float) |
| `ElementDiedContext` | `Entity`, `Element`, `Attacker`, `Damage` (DamageInfo) |
| `ElementMalfunctionContext` | `Element`, `Skill` |
| `SuppressedContext` | `Actor` |
| `SuppressionAppliedContext` | `Actor`, `Change` (float), `Suppressor` (Entity) |
| `MoraleStateChangedContext` | `Actor`, `State` (MoraleState) |
| `BleedingOutContext` | `Leader` (BaseUnitLeader), `RemainingRounds` (int) |
| `StabilizedContext` | `Leader` (BaseUnitLeader), `Savior` (Actor) |
| `ActorStateChangedContext` | `Actor`, `OldState`, `NewState` (ActorState) |
| `HitpointsChangedContext` | `Entity`, `Percent` (float), `AnimationMs` (int) |
| `ArmorChangedContext` | `Entity`, `Durability` (float), `Armor` (int), `AnimationMs` (int) |
| `DiscoveredContext` | `Entity`, `Discoverer` (Actor) |
| `VisibleToPlayerContext` | `Actor` |
| `HiddenToPlayerContext` | `Actor` |
| `MovementStartedContext` | `Actor`, `FromTile`, `ToTile` (Tile), `MovementAction`, `Container` (Entity) |
| `MovementFinishedContext` | `Actor`, `Tile` |
| `SkillUsedContext` | `User` (Actor), `Skill`, `Tile` |
| `SkillCompletedContext` | `Skill` |
| `SkillAddedContext` | `Receiver` (Actor), `Skill`, `Source` (Actor), `Success` (bool) |
| `OffmapAbilityUsedContext` | `Ability` (OffmapAbilityTemplate), `Tile` |
| `OffmapAbilityCanceledContext` | `Ability` (OffmapAbilityTemplate) |

### Strategy

Fired in the campaign meta-game (the strategy layer between missions).

| Context | Payload |
| --- | --- |
| `FactionTrustChangedContext` | `Faction` (StoryFaction), `OldTrust`, `NewTrust` (int) |
| `FactionStatusChangedContext` | `Faction` (StoryFaction), `OldStatus`, `NewStatus` (StoryFactionStatus) |
| `FactionUpgradeUnlockedContext` | `Faction` (StoryFaction), `Upgrade` (ShipUpgradeTemplate) |
| `AliveSquaddiesChangedContext` | `AliveCount` (int) |
| `ConversationVarChangedContext` | `Name` (string), `OldValue`, `NewValue` (int) |
| `LeaderHiredContext` | `Leader` (BaseUnitLeader) |
| `LeaderDismissedContext` | `Leader` (BaseUnitLeader) |
| `LeaderPermadeathContext` | `Leader` (BaseUnitLeader) |
| `LeaderPerkAddedContext` | `Leader` (BaseUnitLeader), `Perk` (PerkTemplate) |
| `OperationStartedContext` | `Operation`, `Mission` |
| `OperationFinishedContext` | `Operation` |
| `BlackMarketItemAddedContext` | `Item` (BaseItem) |
| `BlackMarketRestockedContext` | — |
