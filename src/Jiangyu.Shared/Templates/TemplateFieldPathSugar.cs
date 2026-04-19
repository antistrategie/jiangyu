namespace Jiangyu.Shared.Templates;

/// <summary>
/// Compile-time-semantic sugar for template field paths. The patch pipeline
/// runs modder-authored paths through this rewriter before validation, so
/// modders can write <c>InitialAttributes.Agility</c> instead of memorising
/// the byte offset. Rewrites are explicit, deterministic, and fail loudly on
/// unknown attribute names per principle #7 — no runtime heuristic.
///
/// The mapping for <c>UnitLeaderTemplate.InitialAttributes</c> is seeded from
/// the <c>UnitLeaderAttribute</c> enum in Assembly-CSharp (Agility=0 through
/// Positioning=6). See
/// <c>docs/research/verified/unitleader-initial-attributes.md</c> for source
/// citations and the live-verification record.
/// </summary>
public static class TemplateFieldPathSugar
{
    private const string UnitLeaderTemplateName = "UnitLeaderTemplate";
    private const string InitialAttributesPrefix = "InitialAttributes.";

    // Byte offsets mirror UnitLeaderAttribute enum values exactly. Kept here
    // rather than derived from live reflection so the rewrite is deterministic
    // at compile time and the offset table is reviewable in source.
    private static readonly Dictionary<string, int> UnitLeaderAttributeOffsets = new(StringComparer.Ordinal)
    {
        ["Agility"] = 0,
        ["WeaponSkill"] = 1,
        ["Valour"] = 2,
        ["Toughness"] = 3,
        ["Vitality"] = 4,
        ["Precision"] = 5,
        ["Positioning"] = 6,
    };

    /// <summary>
    /// Rewrites a known sugar path into its canonical indexed form. Returns
    /// <c>rewrittenPath = fieldPath</c> and <c>error = null</c> when no sugar
    /// applies. Returns a non-null error when the sugar prefix matches but
    /// the attribute name is unknown.
    /// </summary>
    public static RewriteResult Rewrite(string? templateType, string? fieldPath)
    {
        if (string.IsNullOrEmpty(fieldPath) || string.IsNullOrEmpty(templateType))
            return new RewriteResult(fieldPath, null, false);

        if (!string.Equals(templateType, UnitLeaderTemplateName, StringComparison.Ordinal))
            return new RewriteResult(fieldPath, null, false);

        if (!fieldPath!.StartsWith(InitialAttributesPrefix, StringComparison.Ordinal))
            return new RewriteResult(fieldPath, null, false);

        var remainder = fieldPath[InitialAttributesPrefix.Length..];
        var nextDot = remainder.IndexOf('.');
        var attributeName = nextDot < 0 ? remainder : remainder[..nextDot];
        var tail = nextDot < 0 ? string.Empty : remainder[nextDot..];

        if (!UnitLeaderAttributeOffsets.TryGetValue(attributeName, out var offset))
        {
            var valid = string.Join(", ", UnitLeaderAttributeOffsets.Keys);
            return new RewriteResult(
                fieldPath,
                $"unknown InitialAttributes name '{attributeName}' on UnitLeaderTemplate. Valid names: {valid}.",
                false);
        }

        return new RewriteResult($"InitialAttributes[{offset}]{tail}", null, true);
    }

    public readonly struct RewriteResult(string? path, string? error, bool rewritten)
    {
        public string? Path { get; } = path;
        public string? Error { get; } = error;
        public bool Rewritten { get; } = rewritten;
    }
}
