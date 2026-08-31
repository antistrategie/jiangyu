using System.Text.RegularExpressions;
using Jiangyu.Shared.Localisation;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Localisation;

/// <summary>
/// Builds a mod's translation source catalogue (the POT). It collects every translatable string a
/// mod ships: each <c>m_DefaultTranslation</c> a clone or patch writes, at any descent depth and
/// inside any composite it constructs (including elements it appends to a list), plus code strings
/// from literal <c>Locale.Text("key","fallback")</c> calls and UXML labels named <c>name="@key"</c>.
/// Translators fill in the resulting <c>&lt;mod&gt;.po</c>. Turning a filled PO back into the
/// loader's apply manifests is <see cref="LocaleTable"/> in Jiangyu.Shared, used by the loader, so a
/// translation mod ships its PO directly with no compiled table.
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

        // Append positions are counted per TARGET, not per patch block: the loader merges every block
        // and every mod's operations for one template into a single stream before applying, so blocks
        // that both append to a collection land one after the other and a per-block count would place
        // the earlier block's elements too near the end.
        var appendPositions = MapAppendPositions(templates.TemplatePatches, out var unstableAppends);

        foreach (var patch in templates.TemplatePatches)
        {
            var templateType = string.IsNullOrEmpty(patch.TemplateType) ? "EntityTemplate" : patch.TemplateType!;

            void Emit(string path, string source)
            {
                var key = LocaleCoordinate.Build(modName, templateType, patch.TemplateId, path);
                if (!seen.Add(key))
                    return;
                var entry = new PoEntry { Context = key, Id = source, Str = string.Empty };
                entry.ExtractedComments.Add($"{CategoryFor(templateType)} · {templateType} {patch.TemplateId} · {path}");
                po.Entries.Add(entry);
            }

            foreach (var op in patch.Set)
            {
                if (TryReadLocalisedWrite(op, out var path, out var source))
                    Emit(path, source);
                else if (IsLocalisedWrite(op))
                    skipped++;

                // A composite value builds a fresh instance, so any localised line inside it is
                // authored text too, however deep. Appended elements have no absolute index until
                // apply time, so they are addressed from the end.
                var prefix = PrefixFor(op, appendPositions, unstableAppends);
                if (prefix == null)
                {
                    skipped += CountLocalisedWrites(op.Value);
                    continue;
                }
                WalkComposite(op.Value, prefix, Emit, ref skipped);
            }

            if (templateType == "ConversationTemplate")
                ExtractConversationSubtitles(patch, modName, po, seen);
        }

        return po;
    }

    // The descent path naming the instance an op's composite value becomes, or null when the op's
    // position cannot be named. A Set writes a member outright, so its path is the descent plus the
    // field. An Append lands at the end, so the j-th of a field's k appends sits k-j back from the
    // end once the patch has run.
    private static string? PrefixFor(
        CompiledTemplateSetOperation op,
        IReadOnlyDictionary<CompiledTemplateSetOperation, int> appendPositions,
        IReadOnlySet<(string Descent, string Field)> unstableAppends)
    {
        if (op.Value?.Composite == null && op.Value?.TypeConstruction == null)
            return null;
        if (string.IsNullOrEmpty(op.FieldPath))
            return null;

        var descent = op.Descent is { Count: > 0 } ? LocaleCoordinate.EncodeDescent(op.Descent) : string.Empty;
        if (descent == null)
            return null;
        var head = descent.Length > 0 ? descent + "/" : string.Empty;

        // A Set with index= replaces one element (KDL `set "Field" index=N type="X" { ... }`), so the
        // element it writes is what the coordinate has to name.
        if (op.Op == CompiledTemplateOp.Set)
            return op.Index is { } index and >= 0
                ? $"{head}{op.FieldPath}[{index}]"
                : head + op.FieldPath;

        if (op.Op != CompiledTemplateOp.Append)
            return null;
        if (unstableAppends.Contains(AppendGroup(op)) || !appendPositions.TryGetValue(op, out var fromEnd))
            return null;
        return $"{head}{op.FieldPath}[^{fromEnd}]";
    }

    // From-end positions for every Append op, and the collection groups whose appends cannot be
    // addressed at all. Appends to one collection land in op order, so counting them gives each a
    // fixed distance from the end. A Clear, Remove or InsertAt on the same collection AFTER an append
    // moves elements the appends already placed, so that group is given up rather than mis-addressed.
    // The whole manifest's appends, grouped by the template each block targets so blocks sharing a
    // target are counted as the one stream the loader will apply.
    private static Dictionary<CompiledTemplateSetOperation, int> MapAppendPositions(
        IReadOnlyList<CompiledTemplatePatch> patches, out IReadOnlySet<(string Descent, string Field)> unstable)
    {
        var byTarget = new Dictionary<(string Type, string Id), List<CompiledTemplateSetOperation>>();
        foreach (var patch in patches)
        {
            var key = (string.IsNullOrEmpty(patch.TemplateType) ? "EntityTemplate" : patch.TemplateType!, patch.TemplateId);
            if (!byTarget.TryGetValue(key, out var ops))
                byTarget[key] = ops = [];
            ops.AddRange(patch.Set);
        }

        var positions = new Dictionary<CompiledTemplateSetOperation, int>();
        var spoiled = new HashSet<(string Descent, string Field)>();
        foreach (var ops in byTarget.Values)
        {
            var targetPositions = MapAppendPositions(ops, out var targetUnstable);
            foreach (var (op, fromEnd) in targetPositions)
                positions[op] = fromEnd;
            foreach (var group in targetUnstable)
                spoiled.Add(group);
        }

        unstable = spoiled;
        return positions;
    }

    private static Dictionary<CompiledTemplateSetOperation, int> MapAppendPositions(
        IReadOnlyList<CompiledTemplateSetOperation> ops, out IReadOnlySet<(string Descent, string Field)> unstable)
    {
        var appendsByGroup = new Dictionary<(string, string), List<CompiledTemplateSetOperation>>();
        var spoiled = new HashSet<(string Descent, string Field)>();

        foreach (var op in ops)
        {
            var group = AppendGroup(op);
            if (op.Op == CompiledTemplateOp.Append)
            {
                if (!appendsByGroup.TryGetValue(group, out var list))
                    appendsByGroup[group] = list = [];
                list.Add(op);
                continue;
            }

            if (op.Op is CompiledTemplateOp.Clear or CompiledTemplateOp.Remove or CompiledTemplateOp.InsertAt
                && appendsByGroup.ContainsKey(group))
                spoiled.Add(group);
        }

        // Keyed by op identity: the model declares no value equality, so two structurally identical
        // appends stay distinct entries.
        var positions = new Dictionary<CompiledTemplateSetOperation, int>();
        foreach (var (group, list) in appendsByGroup)
        {
            if (spoiled.Contains(group))
                continue;
            for (var i = 0; i < list.Count; i++)
                positions[list[i]] = list.Count - i;
        }

        unstable = spoiled;
        return positions;
    }

    // Identifies the collection an op targets: its descent prefix plus the field name. Ops with an
    // unencodable descent share the null group, which is never addressable anyway.
    private static (string Descent, string Field) AppendGroup(CompiledTemplateSetOperation op)
    {
        var descent = op.Descent is { Count: > 0 } ? LocaleCoordinate.EncodeDescent(op.Descent) : string.Empty;
        return (descent ?? string.Empty, op.FieldPath ?? string.Empty);
    }

    // Collect every m_DefaultTranslation inside a composite, recursing through nested composites and
    // through elements the composite itself appends. `prefix` is the descent path to this instance.
    private static void WalkComposite(
        CompiledTemplateValue? value, string prefix, Action<string, string> emit, ref int skipped)
    {
        var composite = value?.Composite ?? value?.TypeConstruction;
        if (composite == null)
            return;

        var appendPositions = MapAppendPositions(composite.Operations, out var unstableAppends);
        foreach (var op in composite.Operations)
        {
            if (op.Op == CompiledTemplateOp.Set
                && op.FieldPath == LocaleCoordinate.DefaultTranslationMember
                && op.Value is { Kind: CompiledTemplateValueKind.String, String: { } text })
            {
                // The composite IS the localised line: its own descent path is the coordinate.
                var descent = op.Descent is { Count: > 0 } ? LocaleCoordinate.EncodeDescent(op.Descent) : string.Empty;
                if (descent == null)
                    skipped++;
                else
                    emit(descent.Length > 0 ? $"{prefix}/{descent}" : prefix, text);
                continue;
            }

            var inner = PrefixFor(op, appendPositions, unstableAppends);
            if (inner == null)
                skipped += CountLocalisedWrites(op.Value);
            else
                WalkComposite(op.Value, $"{prefix}/{inner}", emit, ref skipped);
        }
    }

    // How many localised strings a value carries, for the coverage report when its position could not
    // be named and the whole subtree is given up.
    private static int CountLocalisedWrites(CompiledTemplateValue? value)
    {
        var composite = value?.Composite ?? value?.TypeConstruction;
        if (composite == null)
            return 0;

        var count = 0;
        foreach (var op in composite.Operations)
        {
            if (op.FieldPath == LocaleCoordinate.DefaultTranslationMember
                && op.Value is { Kind: CompiledTemplateValueKind.String })
                count++;
            else
                count += CountLocalisedWrites(op.Value);
        }
        return count;
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

    // Matches new LocalisedText("key", "fallback"): a translatable string declared as DATA (stored in
    // a table or field, resolved at display) rather than via a runtime Locale.Text call. Lets a mod's
    // data-driven UI strings, whose runtime Locale.Text uses computed args, still reach the POT.
    private static readonly Regex LocalisedTextCtor = new(
        """\bLocalisedText\s*\(\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)""",
        RegexOptions.Compiled);

    /// <summary>The <c>(key, fallback)</c> pairs from every literal <c>Locale.Text</c> call and
    /// <c>new LocalisedText(...)</c> declaration in a source file, so a mod's UI strings (live and
    /// data-declared) reach the POT without a separate authoring step.</summary>
    public static IEnumerable<(string Key, string Fallback)> ExtractUiKeys(string sourceText)
    {
        foreach (Match match in LocaleTextCall.Matches(sourceText))
            yield return (PoFormat.Unescape(match.Groups[1].Value), PoFormat.Unescape(match.Groups[2].Value));
        foreach (Match match in LocalisedTextCtor.Matches(sourceText))
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
