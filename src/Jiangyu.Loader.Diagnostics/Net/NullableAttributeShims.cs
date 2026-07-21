// Lambdas over delegates declared in nullable-enabled assemblies (Jiangyu.Shared's
// session events) make the compiler embed nullability metadata. The reference set
// resolves System.Runtime.CompilerServices types against Il2Cppmscorlib, whose wrappers
// carry no usable constructors, so this assembly supplies the attributes itself; a
// source declaration outranks any reference.
#pragma warning disable IDE0130
namespace System.Runtime.CompilerServices;
#pragma warning restore IDE0130

[AttributeUsage(AttributeTargets.All)]
internal sealed class NullableAttribute : Attribute
{
    public NullableAttribute(byte _)
    {
    }

    public NullableAttribute(byte[] _)
    {
    }
}

[AttributeUsage(AttributeTargets.All)]
internal sealed class NullableContextAttribute : Attribute
{
    public NullableContextAttribute(byte _)
    {
    }
}
