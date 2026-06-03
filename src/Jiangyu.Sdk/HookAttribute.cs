namespace Jiangyu.Sdk;

/// <summary>
/// Names a hook context record: the typed payload delivered to
/// <see cref="IHookBus.Subscribe{T}"/> for a global, no-anchor moment (every kill,
/// a round boundary, save or load). The bus is observer-only and keys on the context
/// type, so this is a catalogue marker giving each moment a stable display name.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class HookAttribute : Attribute
{
    public HookAttribute(string name) => Name = name;

    /// <summary>The moment this context represents, for example "Kill".</summary>
    public string Name { get; }
}
