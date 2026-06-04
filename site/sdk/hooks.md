# Hooks

Hooks are the SDK's event bus: global game moments your code reacts to, a kill, a round boundary, a leader hired, a save or load. A **behaviour mod** subscribes to the ones it cares about and runs logic when they fire.

The [entry point](/sdk/#the-entry-point) that hosts a subscription, the [`Context`](/sdk/#the-mod-api) it hangs off, and the `code/` project are shared across the SDK and covered in the [SDK overview](/sdk/). This page is the hook surface itself.

## Subscribing

Subscribe by the context type from your mod's [`OnInit`](/sdk/#the-entry-point). The subscription lives for the mod's lifetime.

```csharp
Context.Hooks.Subscribe<EntityDiedContext>(ctx =>
{
    var victim = ctx.Victim as Entity;   // game-typed payloads are object, cast them
    Log.Info($"{victim?.GetName()} died");
});
```

Every moment is a context type. The [hook reference](#hook-reference) below lists all of them with their payloads, split into a tactical family (combat, turns, movement, skills) and a strategy family (factions, leaders, operations). Game-typed payloads are held as `object` to keep the SDK game-agnostic, so you cast them in your handler (the reference notes which game type each is). Primitive payloads, a round number or a count, are typed directly.

The bus is observer-only: it tells you a moment happened, it does not let you cancel it. To change what a skill or effect does, write a [template type](./template-types). To read or command the live game from a handler (spawn a unit, query a path), use [game verbs](./verbs). To intercept a method with no hook, use [Patches](/sdk/#patches).

## Hook reference

Every context below is a type you pass to `Context.Hooks.Subscribe<T>`. Payload fields without a noted primitive type are game objects held as `object`: cast them to the named game type (for example `ctx.Victim as Entity`). Moments with no payload are marked `(none)`.

### Tactical

Fired off the mission's `TacticalManager`, live only while a tactical mission is loaded.

| Context | Payload |
| --- | --- |
| `RoundStartedContext` | `Round` (int) |
| `MissionStartedContext` | (none) |
| `MissionFinishedContext` | (none) |
| `ObjectiveStateChangedContext` | `Objective`, `OldState`, `NewState` (ObjectiveState) |
| `EntitySpawnedContext` | `Entity` |
| `TurnStartedContext` | `Actor` |
| `TurnEndedContext` | `Actor` |
| `ActiveActorChangedContext` | `Actor` |
| `ActorActedContext` | `Actor` |
| `PlayerTurnContext` | (none) |
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
| `BlackMarketRestockedContext` | (none) |
