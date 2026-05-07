using TinySerializer.Core.Misc;

namespace Jiangyu.Core.Templates.Odin;

/// <summary>
/// TinySerializer binder used during read-only Odin payload walks. Each unique
/// type-name string seen in the stream is associated with a unique sentinel
/// <see cref="Type"/> so the decoder can recover the original name from any
/// <see cref="Type"/> the reader hands back (including TypeID-cached entries
/// that bypass the binder on lookup).
/// </summary>
/// <remarks>
/// The Sirenix binary format declares each new type as a TypeName entry
/// (id + full-name string) and refers to it from later positions via TypeID
/// entries that resolve through the reader's internal id-to-Type cache. We
/// don't want to load real CLR types here (we're walking offline blobs against
/// game assemblies that aren't loaded), so we issue a unique closed-generic
/// sentinel per name. The closed generic is built by chaining
/// <see cref="List{T}"/> wrappers to the requested depth, which guarantees
/// unique <see cref="Type"/> identity per call without requiring runtime IL
/// emission or a Type subclass.
/// </remarks>
internal sealed class OdinTypeNameBinder : TwoWaySerializationBinder
{
    private readonly Dictionary<string, Type> _nameToSentinel = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _sentinelToName = new();

    public override Type BindToType(string typeName, DebugContext? debugContext = null)
    {
        if (typeName is null)
            return typeof(object);

        if (_nameToSentinel.TryGetValue(typeName, out var existing))
            return existing;

        var sentinel = MakeSentinel(_nameToSentinel.Count);
        _nameToSentinel[typeName] = sentinel;
        _sentinelToName[sentinel] = typeName;
        return sentinel;
    }

    public override string BindToName(Type type, DebugContext? debugContext = null)
        => type is not null && _sentinelToName.TryGetValue(type, out var name)
            ? name
            : type?.FullName ?? string.Empty;

    public override bool ContainsType(string typeName)
        => _nameToSentinel.ContainsKey(typeName);

    /// <summary>
    /// Returns the original Sirenix-format type-name string for a sentinel
    /// previously issued by this binder, or null if the type was not issued
    /// by us. Sirenix emits type names in <see cref="Type.AssemblyQualifiedName"/>
    /// form (e.g. <c>Menace.Tactical.Foo, Assembly-CSharp</c>); for display
    /// callers prefer <see cref="TryResolveDisplayName"/>.
    /// </summary>
    public string? TryResolveName(Type? type)
        => type is not null && _sentinelToName.TryGetValue(type, out var name)
            ? name
            : null;

    /// <summary>
    /// Display-friendly variant: returns the qualified name minus the
    /// trailing assembly suffix. <c>Menace.Tactical.Foo, Assembly-CSharp</c>
    /// becomes <c>Menace.Tactical.Foo</c>; <c>System.Int32[], mscorlib</c>
    /// becomes <c>System.Int32[]</c>. Untouched when no comma is present.
    /// </summary>
    public string? TryResolveDisplayName(Type? type)
    {
        var raw = TryResolveName(type);
        return StripAssemblySuffix(raw);
    }

    /// <summary>
    /// Removes the <c>, AssemblyName</c> suffix from an
    /// <see cref="Type.AssemblyQualifiedName"/>-style string. Tolerates
    /// generic-argument lists where commas appear inside square brackets by
    /// only splitting at the first top-level comma.
    /// </summary>
    public static string? StripAssemblySuffix(string? assemblyQualifiedName)
    {
        if (string.IsNullOrEmpty(assemblyQualifiedName))
            return assemblyQualifiedName;

        var depth = 0;
        for (var i = 0; i < assemblyQualifiedName.Length; i++)
        {
            var c = assemblyQualifiedName[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == ',' && depth == 0)
                return assemblyQualifiedName[..i].TrimEnd();
        }
        return assemblyQualifiedName;
    }

    /// <summary>
    /// Builds a deterministic, unique closed-generic <see cref="Type"/> for an
    /// integer index by chaining <c>List&lt;T&gt;</c> to that depth. Each
    /// index produces a Type instance distinct from every other index.
    /// </summary>
    private static Type MakeSentinel(int index)
    {
        Type t = typeof(int);
        for (var i = 0; i < index; i++)
            t = typeof(List<>).MakeGenericType(t);
        return t;
    }
}
