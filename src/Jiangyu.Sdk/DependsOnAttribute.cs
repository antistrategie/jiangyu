using System;

namespace Jiangyu.Sdk;

/// <summary>
/// Declares that this system must initialise after the named sibling systems. The
/// loader orders a mod's systems so every dependency initialises before the systems
/// that depend on it (<see cref="JiangyuSystem.OnInit"/> and the scene and update
/// phases), and unloads after them. A subclass inherits its base system's dependencies.
///
/// <para>Dependencies are resolved across the whole mod, including its other code
/// assemblies. A listed type that is not a system of the mod, the system itself, or
/// null is ignored with a warning, and a dependency cycle is broken by running its
/// members in the stable name order.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class DependsOnAttribute : Attribute
{
    public DependsOnAttribute(params Type[] systems) => Systems = systems ?? Type.EmptyTypes;

    /// <summary>The sibling system types this system must initialise after.</summary>
    public Type[] Systems { get; }
}
