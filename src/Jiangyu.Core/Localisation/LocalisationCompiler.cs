using System.Text.RegularExpressions;
using Jiangyu.Shared.Localisation;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Localisation;

/// <summary>
/// Builds a mod's translation source catalogue (the POT). It collects every translatable string a
/// mod ships: each <c>m_DefaultTranslation</c> a clone or patch sets (at any descent depth), plus
/// code strings from literal <c>Locale.Text("key","fallback")</c> calls and UXML labels named
/// <c>name="@key"</c>. Translators fill in the resulting <c>&lt;mod&gt;.po</c>. Turning a filled PO
/// back into the loader's apply manifests is <see cref="LocaleTable"/> in Jiangyu.Shared, used by
/// the loader, so a translation mod ships its PO directly with no compiled table.
/// </summary>
public static class LocalisationCompiler
{
    /// <summary>Build the POT for a mod from its compiled template program.</summary>
    public static PoFile ExtractCatalogue(CompiledTemplatePatchManifest templates, string modName, out int skipped)
    {
        skipped = 0;
        var po = new PoFile();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (templates.TemplatePatches == null)
            return po;

        foreach (var patch in templates.TemplatePatches)
        {
            var templateType = string.IsNullOrEmpty(patch.TemplateType) ? "EntityTemplate" : patch.TemplateType!;
            foreach (var op in patch.Set)
            {
                if (!TryReadLocalisedWrite(op, out var path, out var source))
                {
                    if (IsLocalisedWrite(op))
                        skipped++;
                    continue;
                }

                var key = LocaleCoordinate.Build(modName, templateType, patch.TemplateId, path);
                if (!seen.Add(key))
                    continue;

                var entry = new PoEntry { Context = key, Id = source, Str = string.Empty };
                entry.ExtractedComments.Add($"{CategoryFor(templateType)} · {templateType} {patch.TemplateId} · {path}");
                po.Entries.Add(entry);
            }

            if (templateType == "ConversationTemplate")
                ExtractConversationSubtitles(patch, modName, po, seen);
        }

        return po;
    }

    // A conversation SAY node's subtitle is a plain Text string on the node, not a LocalizedLine, so it
    // is collected separately: walk the composite tree, and for each SayConversationNode pair its
    // deterministic Guid with its Text. The loader resolves the live node by guid and writes the game's
    // loca entry for it.
    private static void ExtractConversationSubtitles(
        CompiledTemplatePatch patch, string modName, PoFile po, HashSet<string> seen)
    {
        foreach (var op in patch.Set)
            WalkForSayNodes(op.Value);
        return;

        void WalkForSayNodes(CompiledTemplateValue? value)
        {
            var composite = value?.Composite ?? value?.TypeConstruction;
            if (composite == null)
                return;

            if (composite.TypeName.EndsWith("SayConversationNode", StringComparison.Ordinal)
                && TryReadSayNode(composite, out var guid, out var text))
            {
                var key = LocaleCoordinate.BuildConversation(modName, patch.TemplateId, guid);
                if (seen.Add(key))
                {
                    var entry = new PoEntry { Context = key, Id = text, Str = string.Empty };
                    entry.ExtractedComments.Add($"Voice · ConversationTemplate {patch.TemplateId} · say {guid}");
                    po.Entries.Add(entry);
                }
            }

            foreach (var inner in composite.Operations)
                WalkForSayNodes(inner.Value);
        }
    }

    private static bool TryReadSayNode(CompiledTemplateComposite composite, out int guid, out string text)
    {
        guid = 0;
        text = string.Empty;
        var haveGuid = false;
        foreach (var op in composite.Operations)
        {
            if (op.Op != CompiledTemplateOp.Set)
                continue;
            if (op.FieldPath == "Guid" && op.Value is { Kind: CompiledTemplateValueKind.Int32, Int32: { } g })
            {
                guid = g;
                haveGuid = true;
            }
            else if (op.FieldPath == "Text" && op.Value is { Kind: CompiledTemplateValueKind.String, String: { } t })
            {
                text = t;
            }
        }
        return haveGuid && !string.IsNullOrEmpty(text);
    }

    // Matches Locale.Text("key", "fallback") with simple string literals (the common case). Computed
    // keys or fallbacks are not statically extractable and are skipped.
    private static readonly Regex LocaleTextCall = new(
        """\bLocale\s*\.\s*Text\s*\(\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)""",
        RegexOptions.Compiled);

    /// <summary>The <c>(key, fallback)</c> pairs from every literal <c>Locale.Text</c> call in a
    /// source file, so a mod's UI strings reach the POT without a separate authoring step.</summary>
    public static IEnumerable<(string Key, string Fallback)> ExtractUiKeys(string sourceText)
    {
        foreach (Match match in LocaleTextCall.Matches(sourceText))
            yield return (PoFormat.Unescape(match.Groups[1].Value), PoFormat.Unescape(match.Groups[2].Value));
    }

    // A UXML element marked for localisation: name="@<key>", with the authored text="..." as the
    // English fallback (see UI.Localise).
    private static readonly Regex UxmlLocElement = new(
        """<[^>]*(?<![\w-])name\s*=\s*"@([^"]*)"[^>]*>""", RegexOptions.Compiled);
    private static readonly Regex UxmlTextAttr = new(
        """\btext\s*=\s*"([^"]*)""", RegexOptions.Compiled);

    /// <summary>The <c>(key, fallback)</c> pairs from every <c>name="@key"</c>-marked element in a
    /// UXML file, the authored <c>text</c> as the fallback.</summary>
    public static IEnumerable<(string Key, string Fallback)> ExtractUxmlUiKeys(string uxmlText)
    {
        foreach (Match element in UxmlLocElement.Matches(uxmlText))
        {
            var key = element.Groups[1].Value;
            var text = UxmlTextAttr.Match(element.Value);
            yield return (key, text.Success ? text.Groups[1].Value : key);
        }
    }

    private static bool TryReadLocalisedWrite(CompiledTemplateSetOperation op, out string path, out string source)
    {
        path = string.Empty;
        source = string.Empty;

        if (op.Op != CompiledTemplateOp.Set)
            return false;

        // In-place edit: set "...Field" { set "m_DefaultTranslation" "..." }, encoded as a descent
        // to the field of any depth.
        if (op.FieldPath == LocaleCoordinate.DefaultTranslationMember
            && op.Descent is { Count: > 0 }
            && op.Value is { Kind: CompiledTemplateValueKind.String, String: { } directValue }
            && LocaleCoordinate.EncodeDescent(op.Descent) is { } encoded)
        {
            path = encoded;
            source = directValue;
            return true;
        }

        // Replace form: set "Field" type="LocalizedLine" { set "m_DefaultTranslation" "..." }.
        if ((op.Descent == null || op.Descent.Count == 0)
            && !string.IsNullOrEmpty(op.FieldPath)
            && op.FieldPath != LocaleCoordinate.DefaultTranslationMember
            && op.Value?.Composite is { } composite)
        {
            foreach (var inner in composite.Operations)
                if (inner.Op == CompiledTemplateOp.Set
                    && inner.FieldPath == LocaleCoordinate.DefaultTranslationMember
                    && inner.Value is { Kind: CompiledTemplateValueKind.String, String: { } innerValue })
                {
                    path = op.FieldPath;
                    source = innerValue;
                    return true;
                }
        }

        return false;
    }

    // A m_DefaultTranslation string write recognised as localised text but not encodable (a descent
    // step with no field name). Counted so the compiler can report coverage rather than drop silently.
    private static bool IsLocalisedWrite(CompiledTemplateSetOperation op)
        => op.Op == CompiledTemplateOp.Set
           && op.FieldPath == LocaleCoordinate.DefaultTranslationMember
           && op.Value is { Kind: CompiledTemplateValueKind.String };

    private static string CategoryFor(string templateType) => templateType switch
    {
        "WeaponTemplate" or "ItemTemplate" or "ArmorTemplate" or "CommodityTemplate" or "ConsumableTemplate" => "Items",
        "EntityTemplate" => "Entities",
        "SpeakerTemplate" => "Speakers",
        "UnitLeaderTemplate" => "UnitLeaders",
        "SkillTemplate" or "PerkTemplate" => "Skills",
        "TagTemplate" => "Tags",
        _ => templateType,
    };
}
