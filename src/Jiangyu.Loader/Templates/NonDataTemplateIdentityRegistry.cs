namespace Jiangyu.Loader.Templates;

/// <summary>
/// Some non-DataTemplate ScriptableObject types are referenced by modders
/// using a field other than the asset's <c>Object.name</c>. The default
/// non-DataTemplate clone path resolves the source via
/// <c>Resources.FindObjectsOfTypeAll&lt;T&gt;</c> filtered by Object.name,
/// which works for types where the name is the canonical identifier
/// (SoundBank, PerkTreeTemplate, ...). For other types the asset's
/// Object.name is non-unique and a different field carries the
/// modder-facing identity. This registry records those overrides.
///
/// <para>Current entries:</para>
/// <list type="bullet">
///   <item><description><c>ConversationTemplate</c>: <c>Object.name</c> is
///     the short trigger name (e.g. <c>click_bark</c>), shared across every
///     speaker. The unique identifier is the template's <c>Path</c> field
///     (e.g. <c>JeanSy/click_bark</c>), which is what the conversation
///     matcher uses at runtime too.</description></item>
/// </list>
///
/// <para>The registry also names the Resources folder for types whose assets
/// nothing has loaded by the time the clone pass runs. <c>FindObjectsOfTypeAll</c>
/// only sees loaded assets, so a source in such a folder is found by loading the
/// folder first (<see cref="GetResourcesFolder"/>). Types absent from that map are
/// live already when they are cloned: SoundBank and PerkTreeTemplate arrive as
/// dependencies of the skill and unit-leader templates the pass materialises.</para>
/// </summary>
internal static class NonDataTemplateIdentityRegistry
{
    // Both the short type name (for catalogue resolution by simple name)
    // and the Il2Cpp-qualified FullName (for direct Type.FullName lookup).
    private static readonly Dictionary<string, string> IdentityFields = new(StringComparer.Ordinal)
    {
        { "ConversationTemplate", "Path" },
        { "Il2CppMenace.Conversations.ConversationTemplate", "Path" },
    };

    private static readonly Dictionary<string, string> ResourcesFolders = new(StringComparer.Ordinal)
    {
        { "ConversationTemplate", "Data/Conversations" },
        { "Il2CppMenace.Conversations.ConversationTemplate", "Data/Conversations" },
    };

    public static string GetIdentityField(string templateTypeName, Type resolvedType)
        => Lookup(IdentityFields, templateTypeName, resolvedType);

    /// <summary>
    /// The Resources folder holding a non-DataTemplate type's assets, when those
    /// need loading before a by-name lookup can see them; null for types that are
    /// live by the time they are cloned.
    /// </summary>
    public static string GetResourcesFolder(string templateTypeName, Type resolvedType)
        => Lookup(ResourcesFolders, templateTypeName, resolvedType);

    private static string Lookup(Dictionary<string, string> map, string templateTypeName, Type resolvedType)
    {
        if (templateTypeName != null && map.TryGetValue(templateTypeName, out var byName))
            return byName;
        if (resolvedType != null && map.TryGetValue(resolvedType.FullName ?? string.Empty, out var byFullName))
            return byFullName;
        if (resolvedType != null && map.TryGetValue(resolvedType.Name ?? string.Empty, out var byShort))
            return byShort;
        return null;
    }
}
