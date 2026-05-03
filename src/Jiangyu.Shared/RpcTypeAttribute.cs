namespace Jiangyu.Shared;

/// <summary>
/// Marks a class or struct whose shape is part of the RPC contract
/// between the Studio Host and the Studio UI frontend.
/// The <c>Jiangyu.Studio.Rpc.Generators</c> source generator emits a
/// TypeScript interface for every type annotated with this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class RpcTypeAttribute : Attribute
{
}
