using Il2CppMenace.States;
using Il2CppMenace.Strategy;
using Jiangyu.Sdk;

namespace Jiangyu.Game.Strategy;

/// <summary>
/// Campaign-state accessors and the resource-mutation verb. The read-only resource
/// accessor <c>Resource</c> is generated from the verb manifest into this partial class.
/// </summary>
public static partial class Campaign
{
    /// <summary>Whether a campaign (strategy state) is currently loaded.</summary>
    public static bool Active => StrategyState.Get() != null;

    /// <summary>The campaign state, or null when none is loaded.</summary>
    public static StrategyState State => StrategyState.Get();

    /// <summary>
    /// Add <paramref name="delta"/> to a campaign resource (negative to spend) through the
    /// game's own <c>ChangeVar</c>. Returns the resource's new value.
    /// </summary>
    [MutatingVerb]
    public static int ModifyResource(StrategyVars resource, int delta)
    {
        var state = StrategyState.Get();
        state.ChangeVar(resource, delta);
        return state.GetVar(resource);
    }

    /// <summary>
    /// Read a conversation variable by name. The game's accessor takes a character span, so
    /// the name is marshalled into an IL2CPP char array.
    /// </summary>
    public static int ConversationVar(string name)
    {
        var chars = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<char>(name.ToCharArray());
        return StrategyState.Get().GetConversationVarValue(new Il2CppSystem.ReadOnlySpan<char>(chars));
    }
}
