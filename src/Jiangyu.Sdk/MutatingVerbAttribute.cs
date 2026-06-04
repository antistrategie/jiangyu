using System;

namespace Jiangyu.Sdk;

/// <summary>
/// Marks a game-API verb that changes game state. The author-time analyzer flags a
/// call to a <c>[MutatingVerb]</c> method from an override where a mutation is unsafe
/// (a predicate that runs during evaluation, or a polling override that fires
/// repeatedly). Tagging the verb here, rather than naming it in the analyzer, keeps
/// the two from drifting as the verb surface grows.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class MutatingVerbAttribute : Attribute
{
}
