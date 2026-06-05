namespace Jiangyu.Sdk;

// The catalogue of strategy-layer (campaign meta-game) hook moments delivered
// through IHookBus. Unlike the tactical events (one TacticalManager singleton),
// these are scattered across per-instance types, so the loader enumerates the
// factions and subscribes each. Game-typed payloads are held as object (cast in
// the subscriber, e.g. (StoryFaction)ctx.Faction or (StoryFactionStatus)ctx.NewStatus);
// primitives are typed directly.

/// <summary>A story faction's trust value changed.</summary>
public sealed class FactionTrustChangedContext
{
    /// <summary>The faction (a game StoryFaction wrapper).</summary>
    public object Faction { get; init; }

    /// <summary>The previous trust value.</summary>
    public int OldTrust { get; init; }

    /// <summary>The new trust value.</summary>
    public int NewTrust { get; init; }
}

/// <summary>A story faction's status changed (allied / hostile / neutral, etc.).</summary>
public sealed class FactionStatusChangedContext
{
    /// <summary>The faction (a game StoryFaction wrapper).</summary>
    public object Faction { get; init; }

    /// <summary>The previous status (a game StoryFactionStatus enum).</summary>
    public object OldStatus { get; init; }

    /// <summary>The new status (a game StoryFactionStatus enum).</summary>
    public object NewStatus { get; init; }
}

/// <summary>A story faction unlocked a ship upgrade.</summary>
public sealed class FactionUpgradeUnlockedContext
{
    /// <summary>The faction (a game StoryFaction wrapper).</summary>
    public object Faction { get; init; }

    /// <summary>The unlocked upgrade (a game ShipUpgradeTemplate wrapper).</summary>
    public object Upgrade { get; init; }
}

/// <summary>The count of alive squaddies changed (e.g. one was lost).</summary>
public sealed class AliveSquaddiesChangedContext
{
    /// <summary>The number of squaddies still alive.</summary>
    public int AliveCount { get; init; }
}

/// <summary>A campaign conversation variable changed.</summary>
public sealed class ConversationVarChangedContext
{
    /// <summary>The variable name.</summary>
    public string Name { get; init; }

    /// <summary>The previous value.</summary>
    public int OldValue { get; init; }

    /// <summary>The new value.</summary>
    public int NewValue { get; init; }
}

// --- No game event, published from a Harmony patch on the game method ---

/// <summary>A leader was hired into the roster.</summary>
public sealed class LeaderHiredContext
{
    /// <summary>The hired leader (a game BaseUnitLeader wrapper).</summary>
    public object Leader { get; init; }
}

/// <summary>A leader was dismissed from the roster.</summary>
public sealed class LeaderDismissedContext
{
    /// <summary>The dismissed leader (a game BaseUnitLeader wrapper).</summary>
    public object Leader { get; init; }
}

/// <summary>A leader permanently died.</summary>
public sealed class LeaderPermadeathContext
{
    /// <summary>The leader that died (a game BaseUnitLeader wrapper).</summary>
    public object Leader { get; init; }
}

/// <summary>A leader gained a perk (levelled up).</summary>
public sealed class LeaderPerkAddedContext
{
    /// <summary>The leader (a game BaseUnitLeader wrapper).</summary>
    public object Leader { get; init; }

    /// <summary>The perk gained (a game PerkTemplate wrapper).</summary>
    public object Perk { get; init; }
}

/// <summary>A strategic operation concluded.</summary>
public sealed class OperationFinishedContext
{
    /// <summary>The operation that finished (a game Operation wrapper).</summary>
    public object Operation { get; init; }
}

/// <summary>An item was added to the Black Market.</summary>
public sealed class BlackMarketItemAddedContext
{
    /// <summary>The item added (a game BaseItem wrapper).</summary>
    public object Item { get; init; }
}

/// <summary>A strategic operation began.</summary>
public sealed class OperationStartedContext
{
    /// <summary>The operation that started (a game Operation wrapper).</summary>
    public object Operation { get; init; }

    /// <summary>The first mission of the operation (a game Mission wrapper).</summary>
    public object Mission { get; init; }
}

/// <summary>The Black Market restocked its inventory. No payload.</summary>
public sealed class BlackMarketRestockedContext
{
}
