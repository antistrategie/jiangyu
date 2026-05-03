namespace Jiangyu.Shared;

/// <summary>
/// Compile-time copy of the <c>[RpcType]</c> marker.  Source generators
/// ship their own attribute definition so consuming projects don't need
/// a reference to the generator assembly at runtime.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
internal sealed class RpcTypeAttribute : Attribute
{
}
