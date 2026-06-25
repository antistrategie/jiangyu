using System.Collections.Generic;
using Il2CppMenace.States;
using Il2CppMenace.Strategy;
using Il2CppMenace.Tactical;

namespace Jiangyu.Game.Strategy;

/// <summary>
/// Operation reads. The list-returning accessors flatten the game's
/// <c>IReadOnlyList</c> returns into managed lists; the rest are generated from the
/// verb manifest into this partial class.
/// </summary>
public static partial class Operations
{
    /// <summary>The operations currently available in the campaign.</summary>
    public static IReadOnlyList<Operation> Available()
    {
        var result = new List<Operation>();
        var raw = StrategyState.Get().Operations.GetAvailableOperations()
            .TryCast<Il2CppSystem.Collections.Generic.List<Operation>>();
        if (raw != null)
            for (var i = 0; i < raw.Count; i++)
                result.Add(raw[i]);
        return result;
    }

    /// <summary>The statuses of the campaign's previously finished operations.</summary>
    public static IReadOnlyList<OperationStatus> PreviousResults()
    {
        var result = new List<OperationStatus>();
        var raw = StrategyState.Get().Operations.GetPreviousOperationResults()
            .TryCast<Il2CppSystem.Collections.Generic.List<OperationStatus>>();
        if (raw != null)
            for (var i = 0; i < raw.Count; i++)
                result.Add(raw[i]);
        return result;
    }

    /// <summary>The missions belonging to <paramref name="operation"/>.</summary>
    public static IReadOnlyList<Mission> Missions(Operation operation)
    {
        var result = new List<Mission>();
        var raw = operation.GetMissions()
            .TryCast<Il2CppSystem.Collections.Generic.List<Mission>>();
        if (raw != null)
            for (var i = 0; i < raw.Count; i++)
                result.Add(raw[i]);
        return result;
    }

    /// <summary>
    /// How many operations have finished against the given faction type. Pass a non-null
    /// <paramref name="status"/> to count only those with that result, or null for all. Exposes a
    /// C# nullable (not the game's <c>Il2CppSystem.Nullable</c>) so the verb is callable over the bridge.
    /// </summary>
    public static int FinishedCount(FactionType faction, OperationStatus? status = null)
        => StrategyState.Get().Operations.GetFinishedCountAgainst(faction, Optional(status));

    private static Il2CppSystem.Nullable<T> Optional<T>(T? value) where T : struct
        => value.HasValue ? new Il2CppSystem.Nullable<T>(value.Value) : new Il2CppSystem.Nullable<T>();
}
