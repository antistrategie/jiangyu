// The `unmanaged` generic constraint embeds this attribute into the assembly. The
// loader's reference set resolves System.Runtime.CompilerServices types against
// Il2Cppmscorlib, whose wrapper carries no parameterless constructor, so the loader
// supplies the attribute itself; a source declaration outranks any reference.
#pragma warning disable IDE0130
namespace System.Runtime.CompilerServices;
#pragma warning restore IDE0130

[AttributeUsage(AttributeTargets.All)]
internal sealed class IsUnmanagedAttribute : Attribute
{
}
