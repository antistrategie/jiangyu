using System.Text;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Shared.Localisation;

/// <summary>
/// The coordinate format shared by the compiler (which mints msgctxt keys into the POT) and the
/// loader (which parses them back). A template coordinate is
/// <c>modId::TemplateType/templateId/descent</c>, where the descent is a <c>/</c>-joined path of
/// field steps, each optionally <c>Field[index]</c> for an array element (so a LocalizedLine nested
/// in an array round-trips). A code/UXML coordinate is <c>modId::ui/name</c>. A conversation subtitle
/// (a SAY node's voiced line) is <c>modId::conv/convId/nodeGuid</c>, where the node guid is the
/// deterministic value the compiler fills in, so it is stable across rebuilds.
/// </summary>
public static class LocaleCoordinate
{
    public const string DefaultTranslationMember = "m_DefaultTranslation";
    private const string ContextSeparator = "::";
    private const string UiNamespace = "ui";
    private const string ConversationNamespace = "conv";

    public enum Kind { Template, Ui, Conversation }

    public static string Build(string modName, string templateType, string templateId, string descentPath)
        => $"{modName}{ContextSeparator}{templateType}/{templateId}/{descentPath}";

    public static string BuildConversation(string modName, string convId, int nodeGuid)
        => $"{modName}{ContextSeparator}{ConversationNamespace}/{convId}/{nodeGuid}";

    /// <summary>Encode a descent to its path form, or null when a step has no field name.</summary>
    public static string? EncodeDescent(IReadOnlyList<TemplateDescentStep> descent)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < descent.Count; i++)
        {
            var step = descent[i];
            if (string.IsNullOrEmpty(step.Field))
                return null;
            if (i > 0)
                sb.Append('/');
            sb.Append(step.Field);
            if (step.Index is { } index)
                sb.Append('[').Append(index).Append(']');
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>Parse a descent path back to steps, or null when malformed.</summary>
    public static List<TemplateDescentStep>? ParseDescent(string path)
    {
        var steps = new List<TemplateDescentStep>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0)
                return null;
            var bracket = segment.IndexOf('[');
            if (bracket < 0)
            {
                steps.Add(new TemplateDescentStep { Field = segment });
                continue;
            }
            if (segment[^1] != ']')
                return null;
            var field = segment[..bracket];
            var indexText = segment[(bracket + 1)..^1];
            if (field.Length == 0 || !int.TryParse(indexText, out var index))
                return null;
            steps.Add(new TemplateDescentStep { Field = field, Index = index });
        }
        return steps.Count == 0 ? null : steps;
    }

    public static bool TryParse(string key, out string modName, out Kind kind,
        out string type, out string id, out string path)
    {
        modName = string.Empty;
        kind = Kind.Template;
        type = string.Empty;
        id = string.Empty;
        path = string.Empty;

        var sep = key.IndexOf(ContextSeparator, StringComparison.Ordinal);
        if (sep <= 0)
            return false;
        modName = key[..sep];
        var coord = key[(sep + ContextSeparator.Length)..];
        var segments = coord.Split('/');
        if (segments.Length < 2)
            return false;

        if (segments[0] == UiNamespace)
        {
            kind = Kind.Ui;
            return true;
        }

        // conv / convId / nodeGuid. A conversation id can contain '/', so the guid is the last
        // segment and the id is everything between the namespace and it.
        if (segments[0] == ConversationNamespace)
        {
            kind = Kind.Conversation;
            if (segments.Length < 3)
                return false;
            id = string.Join("/", segments[1..^1]);
            path = segments[^1];
            return id.Length > 0 && path.Length > 0;
        }

        // type / id / descent-path. A template id never contains '/', so id is one segment and the
        // descent path is everything after it.
        if (segments.Length < 3)
            return false;
        type = segments[0];
        id = segments[1];
        path = string.Join("/", segments[2..]);
        return type.Length > 0 && id.Length > 0 && path.Length > 0;
    }
}

/// <summary>One conversation subtitle write: the SAY node (by conversation id and node guid) and the
/// string to install. The loader resolves the live node and writes the game's loca entry for it.</summary>
public sealed class LocaleConversationOp
{
    public LocaleConversationOp(string convId, int nodeGuid, string value)
    {
        ConvId = convId;
        NodeGuid = nodeGuid;
        Value = value;
    }

    public string ConvId { get; }
    public int NodeGuid { get; }
    public string Value { get; }
}

/// <summary>The result of compiling one PO file: the translation table (<c>msgstr</c> ops), the
/// English baseline (<c>msgid</c> ops, used to revert a field on a language switch), the conversation
/// subtitle ops (split the same way), the UI string map, and the count of entries whose key could not
/// be parsed.</summary>
public sealed class LocaleTableResult
{
    public LocaleTableResult(
        CompiledTemplatePatchManifest translations,
        CompiledTemplatePatchManifest baseline,
        IReadOnlyList<LocaleConversationOp> conversationTranslations,
        IReadOnlyList<LocaleConversationOp> conversationBaseline,
        Dictionary<string, string> ui,
        int malformed)
    {
        Translations = translations;
        Baseline = baseline;
        ConversationTranslations = conversationTranslations;
        ConversationBaseline = conversationBaseline;
        Ui = ui;
        Malformed = malformed;
    }

    public CompiledTemplatePatchManifest Translations { get; }
    public CompiledTemplatePatchManifest Baseline { get; }
    public IReadOnlyList<LocaleConversationOp> ConversationTranslations { get; }
    public IReadOnlyList<LocaleConversationOp> ConversationBaseline { get; }
    public Dictionary<string, string> Ui { get; }
    public int Malformed { get; }
}

/// <summary>
/// Turns a translator's PO text into the data the loader applies. Each template entry yields a
/// <c>Set m_DefaultTranslation</c> op for both the translation (from <c>msgstr</c>) and the English
/// baseline (from <c>msgid</c>); each conversation entry yields a <see cref="LocaleConversationOp"/>
/// the same way; UI entries yield a <c>key -&gt; translation</c> map. Used by the loader at load, so a
/// mod ships its PO files directly with no compiled table.
/// </summary>
public static class LocaleTable
{
    public static LocaleTableResult Compile(string poText)
    {
        var po = PoFormat.Parse(poText);
        var translations = new Dictionary<(string type, string id), CompiledTemplatePatch>();
        var baseline = new Dictionary<(string type, string id), CompiledTemplatePatch>();
        var conversationTranslations = new List<LocaleConversationOp>();
        var conversationBaseline = new List<LocaleConversationOp>();
        var ui = new Dictionary<string, string>(StringComparer.Ordinal);
        var malformed = 0;

        foreach (var entry in po.Entries)
        {
            if (entry.Context == null)
                continue;

            if (!LocaleCoordinate.TryParse(entry.Context, out _, out var kind, out var type, out var id, out var path))
            {
                malformed++;
                continue;
            }

            if (kind == LocaleCoordinate.Kind.Ui)
            {
                if (entry.HasUsableTranslation)
                    ui[entry.Context] = entry.Str;
                continue;
            }

            if (kind == LocaleCoordinate.Kind.Conversation)
            {
                if (!int.TryParse(path, out var nodeGuid))
                {
                    malformed++;
                    continue;
                }
                conversationBaseline.Add(new LocaleConversationOp(id, nodeGuid, entry.Id));
                if (entry.HasUsableTranslation)
                    conversationTranslations.Add(new LocaleConversationOp(id, nodeGuid, entry.Str));
                continue;
            }

            var descent = LocaleCoordinate.ParseDescent(path);
            if (descent == null)
            {
                malformed++;
                continue;
            }

            // The baseline reverts the field to English on a switch, so it covers every entry the PO
            // names, translated or not. The baseline and translation ops can share the parsed descent:
            // it is only ever read when applied, never mutated.
            AddOp(baseline, type, id, descent, entry.Id);
            if (entry.HasUsableTranslation)
                AddOp(translations, type, id, descent, entry.Str);
        }

        return new LocaleTableResult(
            new CompiledTemplatePatchManifest { TemplatePatches = [.. translations.Values] },
            new CompiledTemplatePatchManifest { TemplatePatches = [.. baseline.Values] },
            conversationTranslations,
            conversationBaseline,
            ui,
            malformed);
    }

    private static void AddOp(
        Dictionary<(string type, string id), CompiledTemplatePatch> byTarget,
        string type, string id, List<TemplateDescentStep> descent, string value)
    {
        if (!byTarget.TryGetValue((type, id), out var patch))
        {
            patch = new CompiledTemplatePatch { TemplateType = type, TemplateId = id };
            byTarget[(type, id)] = patch;
        }

        patch.Set.Add(new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Set,
            FieldPath = LocaleCoordinate.DefaultTranslationMember,
            Descent = descent,
            Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.String, String = value },
        });
    }
}
