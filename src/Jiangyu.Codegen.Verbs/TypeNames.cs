namespace Jiangyu.Codegen.Verbs;

/// <summary>
/// Maps CLR type names to the C# the generator writes: a keyword for the primitives,
/// a global::-qualified name (with nested-type <c>+</c> flattened to <c>.</c>)
/// otherwise. Pure, so the generators' emission stays unit-testable.
/// </summary>
public static class TypeNames
{
    /// <summary>Map a CLR full type name to emitted C# (keyword for primitives, global:: otherwise).</summary>
    public static string Map(string fullName) => fullName switch
    {
        "System.Void" => "void",
        "System.Int32" => "int",
        "System.Int64" => "long",
        "System.Boolean" => "bool",
        "System.Single" => "float",
        "System.Double" => "double",
        "System.String" => "string",
        _ => "global::" + fullName.Replace('+', '.'),
    };

    /// <summary>Map a reflected type to emitted C#.</summary>
    public static string Of(Type t) => Map(t.FullName ?? t.Name);
}
