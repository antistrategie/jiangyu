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

    public static string GetIdentityField(string templateTypeName, Type resolvedType)
    {
        if (templateTypeName != null && IdentityFields.TryGetValue(templateTypeName, out var byName))
            return byName;
        if (resolvedType != null && IdentityFields.TryGetValue(resolvedType.FullName ?? string.Empty, out var byFullName))
            return byFullName;
        if (resolvedType != null && IdentityFields.TryGetValue(resolvedType.Name ?? string.Empty, out var byShort))
            return byShort;
        return null;
    }
}
