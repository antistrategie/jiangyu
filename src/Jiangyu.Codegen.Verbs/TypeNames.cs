using System.Linq;
using System.Text.RegularExpressions;

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

    /// <summary>Map a reflected type to emitted C#, rendering constructed generics as <c>Name&lt;Arg&gt;</c>.</summary>
    public static string Of(Type t)
    {
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            var def = t.GetGenericTypeDefinition();
            // Strip only the `N arity markers (one per generic nesting level), keeping every
            // namespace and nested-type segment, so a nested type of a generic outer is not
            // truncated at its first backtick. Qualify through Map so the rule lives in one place.
            // (Arg placement for a deeply nested-in-generic type stays naive; no such verb exists.)
            var name = Regex.Replace(def.FullName ?? def.Name, "`[0-9]+", "");
            var args = string.Join(", ", t.GetGenericArguments().Select(Of));
            return Map(name) + "<" + args + ">";
        }
        return Map(t.FullName ?? t.Name);
    }
}
