using System;
using System.Collections.Generic;

namespace Jiangyu.Sdk;

/// <summary>How a hook is anchored to the game.</summary>
public enum HookKind
{
    /// <summary>A subscription to a game C# event (the common case).</summary>
    Event,

    /// <summary>A Harmony postfix on a game method that raises no event.</summary>
    Postfix,

    /// <summary>A moment Jiangyu raises itself (no single game anchor), e.g. mission start.</summary>
    Synthetic,
}

/// <summary>One payload field a hook context carries, for the generated reference.</summary>
/// <param name="Name">The context property name.</param>
/// <param name="Type">The payload type, as the modder reads it (a keyword, or a game type held as <c>object</c>).</param>
/// <param name="Summary">What the field is.</param>
public sealed record HookPayloadField(string Name, string Type, string Summary);

/// <summary>
/// One catalogued hook: the SDK context a mod subscribes to, how it anchors to the
/// game, and its payload. The catalogue (<see cref="HookCatalog"/>) is generated from
/// the hook manifest, so it is the single source the reference docs and tooling read.
/// </summary>
/// <param name="Layer">"Tactical" or "Strategy".</param>
/// <param name="Name">The SDK-facing hook name (the context type without the Context suffix).</param>
/// <param name="ContextType">The context type delivered through <see cref="IHookBus"/>.</param>
/// <param name="Kind">How the hook anchors to the game.</param>
/// <param name="Anchor">The game surface it rides: <c>Type.Event</c> or <c>Type.Method</c>, empty when synthetic.</param>
/// <param name="Summary">What the moment is.</param>
/// <param name="Payload">The context's payload fields.</param>
public sealed record HookDescriptor(
    string Layer,
    string Name,
    Type ContextType,
    HookKind Kind,
    string Anchor,
    string Summary,
    IReadOnlyList<HookPayloadField> Payload);
