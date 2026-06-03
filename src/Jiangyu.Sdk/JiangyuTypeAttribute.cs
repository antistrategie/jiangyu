namespace Jiangyu.Sdk;

/// <summary>
/// Marks a code-defined subtype of a game type that Jiangyu injects into the
/// IL2CPP type system and slots via KDL <c>type=</c>. The optional name is the
/// bare type name. Jiangyu namespaces it as <c>ns:Name</c> using the mod id, so
/// a mod type never collides with a game type or with another mod's type.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class JiangyuTypeAttribute : Attribute
{
    public JiangyuTypeAttribute(string name = null) => Name = name;

    /// <summary>Bare type name. When null, the class name is used.</summary>
    public string Name { get; }

    /// <summary>
    /// The game IL2CPP interfaces this type satisfies, for a type that implements a
    /// game interface (such as a value provider) instead of deriving a game base
    /// class. The proxy renders IL2CPP interfaces as classes, so the type cannot
    /// list them in its C# base list: name them here and define the matching
    /// methods, and the loader wires each method into the interface's vtable slot.
    /// </summary>
    public Type[] Interfaces { get; set; }
}
