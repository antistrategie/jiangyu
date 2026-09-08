using Jiangyu.Loader.Templates;
using Jiangyu.Shared.Bundles;
using Jiangyu.Shared.Localisation;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Loader.Runtime.Localisation;

/// <summary>A mod's parsed PO file: the locale code (from its filename) and the compiled tables.</summary>
internal sealed class LocalePo
{
    public LocalePo(DiscoveredMod mod, string code, LocaleTableResult result)
    {
        Mod = mod;
        Code = code;
        Result = result;
    }

    public DiscoveredMod Mod { get; }
    public string Code { get; }
    public LocaleTableResult Result { get; }
}

/// <summary>What to apply for the active language: the ordered manifest list (English baseline first,
/// then the active language's translations), the conversation subtitle ops (ordered the same way), and
/// the merged UI string map.</summary>
internal sealed class LocalePlan
{
    public LocalePlan(
        List<(DiscoveredMod Mod, CompiledTemplatePatchManifest Templates)> loadList,
        List<LocaleConversationOp> conversations,
        Dictionary<string, string> ui,
        int translatedOps)
    {
        LoadList = loadList;
        Conversations = conversations;
        Ui = ui;
        TranslatedOps = translatedOps;
    }

    public List<(DiscoveredMod Mod, CompiledTemplatePatchManifest Templates)> LoadList { get; }
    public List<LocaleConversationOp> Conversations { get; }
    public Dictionary<string, string> Ui { get; }
    public int TranslatedOps { get; }
}

/// <summary>
/// Decides which manifests to apply for the active language, given the PO files shipped by the loaded
/// mods. Pure (no IO, no game calls) so it is unit-tested directly: the loader handles reading the PO
/// files and applying the resulting plan.
/// </summary>
internal static class LocalePlanner
{
    /// <summary>The part of <paramref name="plan"/> that writes to the templates
    /// <paramref name="inScope"/> accepts (type name and id): the field ops on those templates
    /// and the subtitle ops of those conversations. The UI map and the op count are carried
    /// over unchanged. A pass that registered templates late re-applies this rather than the
    /// whole plan, so text settled on every other template is left as it is.</summary>
    public static LocalePlan ScopeTo(LocalePlan plan, Func<string, string, bool> inScope)
        => Filter(plan, (_, type, id) => inScope(type, id), id => inScope(ConversationType, id));

    /// <summary>The part of <paramref name="plan"/> that does not write the fields of a held
    /// block. <paramref name="blockHeld"/> answers for a block (owner, type name, id): a mod's
    /// translations of its own held block are left out while another mod's translations of the
    /// same template, whose block applied, stay in. <paramref name="templateHeld"/> answers for
    /// a template, for the conversation ops that carry no owner. The load-time apply leaves held
    /// work out so its absence does not keep the apply incomplete, and the scoped path writes
    /// it when it lands.</summary>
    public static LocalePlan Without(LocalePlan plan, Func<string, string, string, bool> blockHeld, Func<string, string, bool> templateHeld)
        => Filter(plan,
            (owner, type, id) => !blockHeld(owner, type, id),
            id => !templateHeld(ConversationType, id));

    private const string ConversationType = "ConversationTemplate";

    // A field op's owner is the mod whose patch the coordinate names, which a translation-only
    // mod's PO may differ from; the PO's own mod stands in when the table carries no owner.
    private static LocalePlan Filter(LocalePlan plan, Func<string, string, string, bool> keepField, Func<string, bool> keepConversation)
    {
        var loadList = new List<(DiscoveredMod Mod, CompiledTemplatePatchManifest Templates)>();
        foreach (var (mod, manifest) in plan.LoadList)
        {
            var patches = manifest.TemplatePatches?
                .Where(patch => keepField(patch.Owner ?? mod.Name, patch.TemplateType, patch.TemplateId))
                .ToList();
            if (patches is { Count: > 0 })
                loadList.Add((mod, new CompiledTemplatePatchManifest { TemplatePatches = patches }));
        }

        var conversations = plan.Conversations
            .Where(op => keepConversation(op.ConvId))
            .ToList();

        return new LocalePlan(loadList, conversations, plan.Ui, plan.TranslatedOps);
    }

    public static LocalePlan Build(
        IReadOnlyList<LocalePo> sources, LocaleResolver.State state, string activeCode, bool revertFirst)
    {
        var baseline = new List<(DiscoveredMod, CompiledTemplatePatchManifest)>();
        var translations = new List<(DiscoveredMod, CompiledTemplatePatchManifest)>();
        var convBaseline = new List<LocaleConversationOp>();
        var convTranslations = new List<LocaleConversationOp>();
        var ui = new Dictionary<string, string>(StringComparer.Ordinal);
        var translatedOps = 0;

        foreach (var po in sources)
        {
            // The baseline reverts fields the new language does not translate, so on a switch it must
            // come from EVERY shipped PO (all codes), not just the active one. At load the templates
            // are already English, so the caller passes revertFirst=false and the baseline is skipped.
            if (revertFirst)
            {
                if (po.Result.Baseline.TemplatePatches is { Count: > 0 })
                    baseline.Add((po.Mod, po.Result.Baseline));
                convBaseline.AddRange(po.Result.ConversationBaseline);
            }

            if (state == LocaleResolver.State.Translatable
                && string.Equals(po.Code, activeCode, StringComparison.Ordinal))
            {
                if (po.Result.Translations.TemplatePatches is { Count: > 0 })
                {
                    translations.Add((po.Mod, po.Result.Translations));
                    foreach (var patch in po.Result.Translations.TemplatePatches)
                        translatedOps += patch.Set.Count;
                }
                convTranslations.AddRange(po.Result.ConversationTranslations);
                translatedOps += po.Result.ConversationTranslations.Count;
                foreach (var kv in po.Result.Ui)
                    ui[kv.Key] = kv.Value;
            }
        }

        // Baseline first (revert), then the active language (overlay). Applying in this order means a
        // translated field wins over its baseline (the later write overwrites the earlier).
        var loadList = new List<(DiscoveredMod, CompiledTemplatePatchManifest)>(baseline.Count + translations.Count);
        loadList.AddRange(baseline);
        loadList.AddRange(translations);

        var conversations = new List<LocaleConversationOp>(convBaseline.Count + convTranslations.Count);
        conversations.AddRange(convBaseline);
        conversations.AddRange(convTranslations);

        return new LocalePlan(loadList, conversations, ui, translatedOps);
    }
}
