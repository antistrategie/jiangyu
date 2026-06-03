namespace Jiangyu.Sdk;

// The catalogue of global, no-anchor tactical moments delivered through IHookBus
// (Context.Hooks.Subscribe<T>). These mirror the game's own TacticalManager event
// surface. Game-typed payloads (and game enums) are held as object so the SDK stays
// game-agnostic; cast them in the subscriber, e.g. (Entity)ctx.Victim,
// (ctx.Actor as Actor), or (ActorState)ctx.NewState. Primitive payloads are typed
// directly. Anything attached to a template/skill is a [JiangyuType] handler, not a
// hook; the bus is observers only.

// --- Round / mission ---

/// <summary>A new tactical round began.</summary>
[Hook("RoundStarted")]
public sealed class RoundStartedContext
{
    /// <summary>The round number that just started.</summary>
    public int Round { get; init; }
}

/// <summary>A tactical mission loaded and is ready. No payload.</summary>
[Hook("MissionStarted")]
public sealed class MissionStartedContext
{
}

/// <summary>A tactical mission concluded. No payload.</summary>
[Hook("MissionFinished")]
public sealed class MissionFinishedContext
{
}

/// <summary>A mission objective changed state (e.g. completed or failed).</summary>
[Hook("ObjectiveStateChanged")]
public sealed class ObjectiveStateChangedContext
{
    /// <summary>The objective (a game Objective wrapper).</summary>
    public object Objective { get; init; }

    /// <summary>The previous state (a game ObjectiveState enum).</summary>
    public object OldState { get; init; }

    /// <summary>The new state (a game ObjectiveState enum).</summary>
    public object NewState { get; init; }
}

/// <summary>An entity was spawned into the mission.</summary>
[Hook("EntitySpawned")]
public sealed class EntitySpawnedContext
{
    /// <summary>The spawned entity (a game Entity wrapper).</summary>
    public object Entity { get; init; }
}

// --- Turn flow ---

/// <summary>An actor's turn began.</summary>
[Hook("TurnStarted")]
public sealed class TurnStartedContext
{
    /// <summary>The actor whose turn started (a game Actor wrapper).</summary>
    public object Actor { get; init; }
}

/// <summary>An actor's turn concluded.</summary>
[Hook("TurnEnded")]
public sealed class TurnEndedContext
{
    /// <summary>The actor whose turn ended (a game Actor wrapper).</summary>
    public object Actor { get; init; }
}

/// <summary>The active actor changed (turn passed to a new actor).</summary>
[Hook("ActiveActorChanged")]
public sealed class ActiveActorChangedContext
{
    /// <summary>The new active actor (a game Actor wrapper).</summary>
    public object Actor { get; init; }
}

/// <summary>An actor finished acting.</summary>
[Hook("ActorActed")]
public sealed class ActorActedContext
{
    /// <summary>The actor that acted (a game Actor wrapper).</summary>
    public object Actor { get; init; }
}

/// <summary>The player gained control for the turn. No payload.</summary>
[Hook("PlayerTurn")]
public sealed class PlayerTurnContext
{
}

/// <summary>An AI faction gained control for the turn.</summary>
[Hook("AITurn")]
public sealed class AITurnContext
{
    /// <summary>The faction identifier taking the turn.</summary>
    public int Faction { get; init; }
}

// --- Combat ---

/// <summary>An entity died.</summary>
[Hook("EntityDied")]
public sealed class EntityDiedContext
{
    /// <summary>The entity that died (a game Entity wrapper).</summary>
    public object Victim { get; init; }

    /// <summary>The entity credited with the kill, or null (a game Entity wrapper).</summary>
    public object Killer { get; init; }
}

/// <summary>An entity received damage.</summary>
[Hook("DamageReceived")]
public sealed class DamageReceivedContext
{
    /// <summary>The entity that took the damage (a game Entity wrapper).</summary>
    public object Victim { get; init; }

    /// <summary>The entity that dealt it, or null (a game Entity wrapper).</summary>
    public object Source { get; init; }

    /// <summary>The skill that dealt it, or null (a game Skill wrapper).</summary>
    public object Skill { get; init; }

    /// <summary>The damage detail (a game DamageInfo wrapper).</summary>
    public object Damage { get; init; }
}

/// <summary>An attack missed its target.</summary>
[Hook("AttackMissed")]
public sealed class AttackMissedContext
{
    /// <summary>The intended target (a game Entity wrapper).</summary>
    public object Target { get; init; }

    /// <summary>The attacker (a game Entity wrapper).</summary>
    public object Attacker { get; init; }

    /// <summary>The skill used (a game Skill wrapper).</summary>
    public object Skill { get; init; }
}

/// <summary>A tile-targeted attack began.</summary>
[Hook("AttackTileStarted")]
public sealed class AttackTileStartedContext
{
    /// <summary>The attacker (a game Actor wrapper).</summary>
    public object Attacker { get; init; }

    /// <summary>The skill used (a game Skill wrapper).</summary>
    public object Skill { get; init; }

    /// <summary>The targeted tile (a game Tile wrapper).</summary>
    public object Tile { get; init; }

    /// <summary>The attack's duration in seconds.</summary>
    public float DurationSeconds { get; init; }
}

/// <summary>A sub-element of an entity was destroyed (e.g. a vehicle part).</summary>
[Hook("ElementDied")]
public sealed class ElementDiedContext
{
    /// <summary>The owning entity (a game Entity wrapper).</summary>
    public object Entity { get; init; }

    /// <summary>The destroyed element (a game Element wrapper).</summary>
    public object Element { get; init; }

    /// <summary>The attacker, or null (a game Entity wrapper).</summary>
    public object Attacker { get; init; }

    /// <summary>The damage detail (a game DamageInfo wrapper).</summary>
    public object Damage { get; init; }
}

/// <summary>A sub-element malfunctioned.</summary>
[Hook("ElementMalfunction")]
public sealed class ElementMalfunctionContext
{
    /// <summary>The element (a game Element wrapper).</summary>
    public object Element { get; init; }

    /// <summary>The skill involved, or null (a game Skill wrapper).</summary>
    public object Skill { get; init; }
}

// --- Suppression / morale / bleeding ---

/// <summary>An actor became suppressed.</summary>
[Hook("Suppressed")]
public sealed class SuppressedContext
{
    /// <summary>The suppressed actor (a game Actor wrapper).</summary>
    public object Actor { get; init; }
}

/// <summary>Suppression was applied to an actor with a specific amount.</summary>
[Hook("SuppressionApplied")]
public sealed class SuppressionAppliedContext
{
    /// <summary>The actor (a game Actor wrapper).</summary>
    public object Actor { get; init; }

    /// <summary>The suppression change applied.</summary>
    public float Change { get; init; }

    /// <summary>The source entity, or null (a game Entity wrapper).</summary>
    public object Suppressor { get; init; }
}

/// <summary>An actor's morale state changed.</summary>
[Hook("MoraleStateChanged")]
public sealed class MoraleStateChangedContext
{
    /// <summary>The actor (a game Actor wrapper).</summary>
    public object Actor { get; init; }

    /// <summary>The new morale state (a game MoraleState enum).</summary>
    public object State { get; init; }
}

/// <summary>A leader entered the bleeding-out state.</summary>
[Hook("BleedingOut")]
public sealed class BleedingOutContext
{
    /// <summary>The leader (a game BaseUnitLeader wrapper).</summary>
    public object Leader { get; init; }

    /// <summary>Rounds remaining before death.</summary>
    public int RemainingRounds { get; init; }
}

/// <summary>A bleeding leader was stabilised.</summary>
[Hook("Stabilized")]
public sealed class StabilizedContext
{
    /// <summary>The stabilised leader (a game BaseUnitLeader wrapper).</summary>
    public object Leader { get; init; }

    /// <summary>The actor that stabilised them (a game Actor wrapper).</summary>
    public object Savior { get; init; }
}

// --- Actor / entity state ---

/// <summary>An actor's state changed (idle / moving / dead, etc.).</summary>
[Hook("ActorStateChanged")]
public sealed class ActorStateChangedContext
{
    /// <summary>The actor (a game Actor wrapper).</summary>
    public object Actor { get; init; }

    /// <summary>The previous state (a game ActorState enum).</summary>
    public object OldState { get; init; }

    /// <summary>The new state (a game ActorState enum).</summary>
    public object NewState { get; init; }
}

/// <summary>An entity's hitpoints changed.</summary>
[Hook("HitpointsChanged")]
public sealed class HitpointsChangedContext
{
    /// <summary>The entity (a game Entity wrapper).</summary>
    public object Entity { get; init; }

    /// <summary>The new hitpoints as a fraction (0..1).</summary>
    public float Percent { get; init; }

    /// <summary>The bar animation duration in milliseconds.</summary>
    public int AnimationMs { get; init; }
}

/// <summary>An entity's armour changed.</summary>
[Hook("ArmorChanged")]
public sealed class ArmorChangedContext
{
    /// <summary>The entity (a game Entity wrapper).</summary>
    public object Entity { get; init; }

    /// <summary>The armour durability remaining.</summary>
    public float Durability { get; init; }

    /// <summary>The armour value.</summary>
    public int Armor { get; init; }

    /// <summary>The bar animation duration in milliseconds.</summary>
    public int AnimationMs { get; init; }
}

// --- Visibility ---

/// <summary>A hidden entity was discovered.</summary>
[Hook("Discovered")]
public sealed class DiscoveredContext
{
    /// <summary>The discovered entity (a game Entity wrapper).</summary>
    public object Entity { get; init; }

    /// <summary>The actor that discovered it (a game Actor wrapper).</summary>
    public object Discoverer { get; init; }
}

/// <summary>An actor became visible to the player.</summary>
[Hook("VisibleToPlayer")]
public sealed class VisibleToPlayerContext
{
    /// <summary>The actor (a game Actor wrapper).</summary>
    public object Actor { get; init; }
}

/// <summary>An actor became hidden from the player.</summary>
[Hook("HiddenToPlayer")]
public sealed class HiddenToPlayerContext
{
    /// <summary>The actor (a game Actor wrapper).</summary>
    public object Actor { get; init; }
}

// --- Movement ---

/// <summary>An actor started moving.</summary>
[Hook("MovementStarted")]
public sealed class MovementStartedContext
{
    /// <summary>The moving actor (a game Actor wrapper).</summary>
    public object Actor { get; init; }

    /// <summary>The tile moved from (a game Tile wrapper).</summary>
    public object FromTile { get; init; }

    /// <summary>The tile moved to (a game Tile wrapper).</summary>
    public object ToTile { get; init; }

    /// <summary>The movement action (a game MovementAction enum).</summary>
    public object MovementAction { get; init; }

    /// <summary>The carrying entity, or null (a game Entity wrapper).</summary>
    public object Container { get; init; }
}

/// <summary>An actor finished moving.</summary>
[Hook("MovementFinished")]
public sealed class MovementFinishedContext
{
    /// <summary>The actor (a game Actor wrapper).</summary>
    public object Actor { get; init; }

    /// <summary>The destination tile (a game Tile wrapper).</summary>
    public object Tile { get; init; }
}

// --- Skills ---

/// <summary>An actor used a skill.</summary>
[Hook("SkillUsed")]
public sealed class SkillUsedContext
{
    /// <summary>The actor that used the skill (a game Actor wrapper).</summary>
    public object User { get; init; }

    /// <summary>The skill used (a game Skill wrapper).</summary>
    public object Skill { get; init; }

    /// <summary>The targeted tile (a game Tile wrapper).</summary>
    public object Tile { get; init; }
}

/// <summary>A skill finished executing.</summary>
[Hook("SkillCompleted")]
public sealed class SkillCompletedContext
{
    /// <summary>The skill (a game Skill wrapper).</summary>
    public object Skill { get; init; }
}

/// <summary>A skill was granted to an actor.</summary>
[Hook("SkillAdded")]
public sealed class SkillAddedContext
{
    /// <summary>The actor that received the skill (a game Actor wrapper).</summary>
    public object Receiver { get; init; }

    /// <summary>The skill granted (a game Skill wrapper).</summary>
    public object Skill { get; init; }

    /// <summary>The granting actor, or null (a game Actor wrapper).</summary>
    public object Source { get; init; }

    /// <summary>Whether the grant succeeded.</summary>
    public bool Success { get; init; }
}

// --- Offmap abilities ---

/// <summary>An offmap ability was used.</summary>
[Hook("OffmapAbilityUsed")]
public sealed class OffmapAbilityUsedContext
{
    /// <summary>The ability template (a game OffmapAbilityTemplate wrapper).</summary>
    public object Ability { get; init; }

    /// <summary>The targeted tile (a game Tile wrapper).</summary>
    public object Tile { get; init; }
}

/// <summary>An offmap ability was cancelled.</summary>
[Hook("OffmapAbilityCanceled")]
public sealed class OffmapAbilityCanceledContext
{
    /// <summary>The ability template (a game OffmapAbilityTemplate wrapper).</summary>
    public object Ability { get; init; }
}
