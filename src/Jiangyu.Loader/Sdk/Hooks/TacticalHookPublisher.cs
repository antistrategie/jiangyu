using System;
using Il2CppMenace.OffmapAbilities;
using Il2CppMenace.Strategy;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Objectives;
using Il2CppMenace.Tactical.Skills;
using Jiangyu.Loader.Logging;
using Jiangyu.Sdk;

namespace Jiangyu.Loader.Sdk.Hooks;

/// <summary>
/// Bridges the game's no-anchor tactical moments onto the hook bus: subscribes to
/// <see cref="TacticalManager"/>'s C# events and republishes them as SDK hook
/// contexts mods receive through <see cref="IHookBus"/>. Each publish guards on
/// <see cref="InProcessHookBus.HasSubscribers{T}"/> so an event with no listener
/// skips the subscriber dispatch. The TacticalManager is recreated each mission,
/// so <see cref="EnsureAttached"/> re-attaches once a fresh one is present, and
/// publishes <see cref="MissionStartedContext"/> at that point.
/// </summary>
internal sealed class TacticalHookPublisher : HookPublisherBase
{
    private IntPtr _attachedTo;

    public TacticalHookPublisher(InProcessHookBus bus, IModHostLog log)
        : base(bus, log)
    {
    }

    /// <summary>
    /// Attach to the current <see cref="TacticalManager"/> when one is present and
    /// not already attached. Cheap and idempotent: a no-op once attached to this
    /// mission's manager. Call each frame while a tactical mission is live.
    /// </summary>
    public void EnsureAttached()
    {
        var tm = TacticalManager.Get();
        if (tm == null || tm.Pointer == IntPtr.Zero || tm.Pointer == _attachedTo)
            return;

        Hook<TacticalManager.OnRoundStartEvent>(tm.add_OnRoundStart, (Action<int>)OnRoundStart, "OnRoundStart");
        Hook<TacticalManager.OnFinishedEvent>(tm.add_OnFinished, (Action)OnMissionFinished, "OnFinished");
        Hook<TacticalManager.OnObjectiveStateChangedEvent>(tm.add_OnObjectiveStateChanged, (Action<Objective, ObjectiveState, ObjectiveState>)OnObjectiveStateChanged, "OnObjectiveStateChanged");
        Hook<TacticalManager.OnEntitySpawnedEvent>(tm.add_OnEntitySpawned, (Action<Entity>)OnEntitySpawned, "OnEntitySpawned");

        Hook<TacticalManager.OnTurnStartEvent>(tm.add_OnTurnStart, (Action<Actor>)OnTurnStart, "OnTurnStart");
        Hook<TacticalManager.OnTurnEndEvent>(tm.add_OnTurnEnd, (Action<Actor, bool>)OnTurnEnd, "OnTurnEnd");
        Hook<TacticalManager.OnActiveActorChangedEvent>(tm.add_OnActiveActorChanged, (Action<Actor>)OnActiveActorChanged, "OnActiveActorChanged");
        Hook<TacticalManager.OnActorActedEvent>(tm.add_OnActorActed, (Action<Actor>)OnActorActed, "OnActorActed");
        Hook<TacticalManager.OnPlayerTurnEvent>(tm.add_OnPlayerTurn, (Action)OnPlayerTurn, "OnPlayerTurn");
        Hook<TacticalManager.OnAITurnEvent>(tm.add_OnAITurn, (Action<int>)OnAITurn, "OnAITurn");

        Hook<TacticalManager.OnDeathEvent>(tm.add_OnDeath, (Action<Entity, Entity>)OnDeath, "OnDeath");
        Hook<TacticalManager.OnDamageReceivedEvent>(tm.add_OnDamageReceived, (Action<Entity, Entity, Skill, DamageInfo>)OnDamageReceived, "OnDamageReceived");
        Hook<TacticalManager.OnAttackMissedEvent>(tm.add_OnAttackMissed, (Action<Entity, Entity, Skill>)OnAttackMissed, "OnAttackMissed");
        Hook<TacticalManager.OnAttackTileStartEvent>(tm.add_OnAttackTileStart, (Action<Actor, Skill, Tile, float>)OnAttackTileStart, "OnAttackTileStart");
        Hook<TacticalManager.OnElementDeathEvent>(tm.add_OnElementDeath, (Action<Entity, Element, Entity, DamageInfo>)OnElementDeath, "OnElementDeath");
        Hook<TacticalManager.OnElementMalfunctionEvent>(tm.add_OnElementMalfunction, (Action<Element, Skill>)OnElementMalfunction, "OnElementMalfunction");

        Hook<TacticalManager.OnSuppressedEvent>(tm.add_OnSuppressed, (Action<Actor>)OnSuppressed, "OnSuppressed");
        Hook<TacticalManager.OnSuppressionAppliedEvent>(tm.add_OnSuppressionApplied, (Action<Actor, float, Entity>)OnSuppressionApplied, "OnSuppressionApplied");
        Hook<TacticalManager.OnMoraleStateChangedEvent>(tm.add_OnMoraleStateChanged, (Action<Actor, MoraleState>)OnMoraleStateChanged, "OnMoraleStateChanged");
        Hook<TacticalManager.OnBleedingOutEvent>(tm.add_OnBleedingOut, (Action<BaseUnitLeader, int>)OnBleedingOut, "OnBleedingOut");
        Hook<TacticalManager.OnStabilizedEvent>(tm.add_OnStabilized, (Action<BaseUnitLeader, Actor>)OnStabilized, "OnStabilized");

        Hook<TacticalManager.OnActorStateChangedEvent>(tm.add_OnActorStateChanged, (Action<Actor, ActorState, ActorState>)OnActorStateChanged, "OnActorStateChanged");
        Hook<TacticalManager.OnHitpointsChangedEvent>(tm.add_OnHitpointsChanged, (Action<Entity, float, int>)OnHitpointsChanged, "OnHitpointsChanged");
        Hook<TacticalManager.OnArmorChangedEvent>(tm.add_OnArmorChanged, (Action<Entity, float, int, int>)OnArmorChanged, "OnArmorChanged");

        Hook<TacticalManager.OnDiscoveredEvent>(tm.add_OnDiscovered, (Action<Entity, Actor>)OnDiscovered, "OnDiscovered");
        Hook<TacticalManager.OnVisibleToPlayerEvent>(tm.add_OnVisibleToPlayer, (Action<Actor>)OnVisibleToPlayer, "OnVisibleToPlayer");
        Hook<TacticalManager.OnHiddenToPlayerEvent>(tm.add_OnHiddenToPlayer, (Action<Actor>)OnHiddenToPlayer, "OnHiddenToPlayer");

        Hook<TacticalManager.OnMovementEvent>(tm.add_OnMovement, (Action<Actor, Tile, Tile, MovementAction, Entity>)OnMovement, "OnMovement");
        Hook<TacticalManager.OnMovementFinishedEvent>(tm.add_OnMovementFinished, (Action<Actor, Tile>)OnMovementFinished, "OnMovementFinished");

        Hook<TacticalManager.OnSkillUseEvent>(tm.add_OnSkillUse, (Action<Actor, Skill, Tile>)OnSkillUse, "OnSkillUse");
        Hook<TacticalManager.OnAfterSkillUseEvent>(tm.add_OnAfterSkillUse, (Action<Skill>)OnSkillCompleted, "OnAfterSkillUse");
        Hook<TacticalManager.OnSkillAddedEvent>(tm.add_OnSkillAdded, (Action<Actor, Skill, Actor, bool>)OnSkillAdded, "OnSkillAdded");

        Hook<TacticalManager.OnOffmapAbilityUsedEvent>(tm.add_OnOffmapAbilityUsed, (Action<OffmapAbilityTemplate, Tile>)OnOffmapAbilityUsed, "OnOffmapAbilityUsed");
        Hook<TacticalManager.OnOffmapAbilityCanceledEvent>(tm.add_OnOffmapAbilityCanceled, (Action<OffmapAbilityTemplate>)OnOffmapAbilityCanceled, "OnOffmapAbilityCanceled");

        _attachedTo = tm.Pointer;
        Log.Info($"hooks: attached to TacticalManager ({HookedEventCount} events)");
        Publish(new MissionStartedContext());
    }

    /// <summary>Forget the current attachment so the next mission re-attaches, and drop
    /// the previous mission's rooted delegates (its TacticalManager is destroyed).</summary>
    public void Reset()
    {
        _attachedTo = IntPtr.Zero;
        ClearHookedDelegates();
    }

    private void OnRoundStart(int round) => Publish(new RoundStartedContext { Round = round });
    private void OnMissionFinished() => Publish(new MissionFinishedContext());
    private void OnObjectiveStateChanged(Objective objective, ObjectiveState oldState, ObjectiveState newState)
        => Publish(new ObjectiveStateChangedContext { Objective = objective, OldState = oldState, NewState = newState });
    private void OnEntitySpawned(Entity entity) => Publish(new EntitySpawnedContext { Entity = entity });

    private void OnTurnStart(Actor actor) => Publish(new TurnStartedContext { Actor = actor });
    private void OnTurnEnd(Actor actor, bool _) => Publish(new TurnEndedContext { Actor = actor });
    private void OnActiveActorChanged(Actor actor) => Publish(new ActiveActorChangedContext { Actor = actor });
    private void OnActorActed(Actor actor) => Publish(new ActorActedContext { Actor = actor });
    private void OnPlayerTurn() => Publish(new PlayerTurnContext());
    private void OnAITurn(int faction) => Publish(new AITurnContext { Faction = faction });

    private void OnDeath(Entity victim, Entity killer) => Publish(new EntityDiedContext { Victim = victim, Killer = killer });
    private void OnDamageReceived(Entity victim, Entity source, Skill skill, DamageInfo damage)
        => Publish(new DamageReceivedContext { Victim = victim, Source = source, Skill = skill, Damage = damage });
    private void OnAttackMissed(Entity target, Entity attacker, Skill skill)
        => Publish(new AttackMissedContext { Target = target, Attacker = attacker, Skill = skill });
    private void OnAttackTileStart(Actor attacker, Skill skill, Tile tile, float duration)
        => Publish(new AttackTileStartedContext { Attacker = attacker, Skill = skill, Tile = tile, DurationSeconds = duration });
    private void OnElementDeath(Entity entity, Element element, Entity attacker, DamageInfo damage)
        => Publish(new ElementDiedContext { Entity = entity, Element = element, Attacker = attacker, Damage = damage });
    private void OnElementMalfunction(Element element, Skill skill)
        => Publish(new ElementMalfunctionContext { Element = element, Skill = skill });

    private void OnSuppressed(Actor actor) => Publish(new SuppressedContext { Actor = actor });
    private void OnSuppressionApplied(Actor actor, float change, Entity suppressor)
        => Publish(new SuppressionAppliedContext { Actor = actor, Change = change, Suppressor = suppressor });
    private void OnMoraleStateChanged(Actor actor, MoraleState state)
        => Publish(new MoraleStateChangedContext { Actor = actor, State = state });
    private void OnBleedingOut(BaseUnitLeader leader, int remainingRounds)
        => Publish(new BleedingOutContext { Leader = leader, RemainingRounds = remainingRounds });
    private void OnStabilized(BaseUnitLeader leader, Actor savior)
        => Publish(new StabilizedContext { Leader = leader, Savior = savior });

    private void OnActorStateChanged(Actor actor, ActorState oldState, ActorState newState)
        => Publish(new ActorStateChangedContext { Actor = actor, OldState = oldState, NewState = newState });
    private void OnHitpointsChanged(Entity entity, float percent, int animationMs)
        => Publish(new HitpointsChangedContext { Entity = entity, Percent = percent, AnimationMs = animationMs });
    private void OnArmorChanged(Entity entity, float durability, int armor, int animationMs)
        => Publish(new ArmorChangedContext { Entity = entity, Durability = durability, Armor = armor, AnimationMs = animationMs });

    private void OnDiscovered(Entity entity, Actor discoverer)
        => Publish(new DiscoveredContext { Entity = entity, Discoverer = discoverer });
    private void OnVisibleToPlayer(Actor actor) => Publish(new VisibleToPlayerContext { Actor = actor });
    private void OnHiddenToPlayer(Actor actor) => Publish(new HiddenToPlayerContext { Actor = actor });

    private void OnMovement(Actor actor, Tile from, Tile to, MovementAction action, Entity container)
        => Publish(new MovementStartedContext { Actor = actor, FromTile = from, ToTile = to, MovementAction = action, Container = container });
    private void OnMovementFinished(Actor actor, Tile tile)
        => Publish(new MovementFinishedContext { Actor = actor, Tile = tile });

    private void OnSkillUse(Actor user, Skill skill, Tile tile)
        => Publish(new SkillUsedContext { User = user, Skill = skill, Tile = tile });
    private void OnSkillCompleted(Skill skill) => Publish(new SkillCompletedContext { Skill = skill });
    private void OnSkillAdded(Actor receiver, Skill skill, Actor source, bool success)
        => Publish(new SkillAddedContext { Receiver = receiver, Skill = skill, Source = source, Success = success });

    private void OnOffmapAbilityUsed(OffmapAbilityTemplate ability, Tile tile)
        => Publish(new OffmapAbilityUsedContext { Ability = ability, Tile = tile });
    private void OnOffmapAbilityCanceled(OffmapAbilityTemplate ability)
        => Publish(new OffmapAbilityCanceledContext { Ability = ability });
}
