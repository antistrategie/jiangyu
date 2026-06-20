using Il2CppInterop.Runtime.InteropTypes;
using Jiangyu.Loader.Templates;
using Jiangyu.Shared.Bundles;
using Jiangyu.Shared.Localisation;
using Jiangyu.Shared.Templates;
using MelonLoader;
using BaseLocalizedString = Il2CppMenace.Tools.BaseLocalizedString;
using ConversationTemplate = Il2CppMenace.Conversations.ConversationTemplate;
using LocaEntryType = Il2CppMenace.Tools.LocaEntryType;
using LocaManager = Il2CppMenace.Tools.LocaManager;

namespace Jiangyu.Loader.Runtime.Localisation;

/// <summary>
/// Applies a locale plan into the game's loca store the way the game does. For each translatable field
/// it writes two places:
/// <list type="bullet">
///   <item>the <c>LocaData</c> entry the UI reads, keyed
///     <c>&lt;Category&gt;/&lt;templateId&gt;/&lt;fieldName&gt;</c> (the category and field name come
///     from the live line, so the key matches what the game builds for its own CSV entries);</item>
///   <item>the live <c>BaseLocalizedString</c>'s default translation, for code paths that read it
///     directly.</item>
/// </list>
/// Dynamically cloned templates get no CSV entry at startup, so without the table write their text never
/// localises. The game rebuilds <c>LocaData</c> on every language switch, so the loader re-runs this
/// after each switch (ops apply in plan order, later overwriting earlier, so a translation wins over its
/// English baseline).
/// </summary>
internal static class LocaleTableInjector
{
    /// <summary>
    /// Apply template-field ops. Returns false when a target template was not live yet, so the caller
    /// retries on the next scene poll.
    /// </summary>
    public static bool Apply(
        IReadOnlyList<(DiscoveredMod Mod, CompiledTemplatePatchManifest Templates)> loadList,
        MelonLogger.Instance log)
    {
        var allResolved = true;

        foreach (var (_, manifest) in loadList)
        {
            if (manifest.TemplatePatches == null)
                continue;

            foreach (var patch in manifest.TemplatePatches)
            {
                if (!TemplateRuntimeAccess.TryGetTemplateById(
                        patch.TemplateType, patch.TemplateId, out var template, out _, out _))
                {
                    // Not live yet (loaded after this poll) or unknown id: retry on the next poll.
                    allResolved = false;
                    continue;
                }

                foreach (var op in patch.Set)
                {
                    if (op.Descent == null || op.Value?.String == null)
                        continue;
                    TryInject(template, patch.TemplateId, op.Descent, op.Value.String, log);
                }
            }
        }

        return allResolved;
    }

    /// <summary>
    /// Apply conversation subtitle ops: for each, resolve the live conversation and write the loca entry
    /// the game reads for the SAY node. The key is the conversation's base key plus the node guid, and
    /// the category data is the conversation's own (<c>GetBaseLocaKey</c> / <c>GetLocaData</c>), so they
    /// match what the game builds when it plays the line. Returns false when a conversation was not live
    /// yet, so the caller retries.
    /// </summary>
    public static bool ApplyConversations(IReadOnlyList<LocaleConversationOp> ops, MelonLogger.Instance log)
    {
        var allResolved = true;

        foreach (var op in ops)
        {
            if (!TemplateRuntimeAccess.TryGetTemplateById(
                    "ConversationTemplate", op.ConvId, out var obj, out _, out _))
            {
                allResolved = false;
                continue;
            }

            var conv = obj.TryCast<ConversationTemplate>();
            if (conv == null)
            {
                log.Warning($"Locale inject: conversation {op.ConvId} is not a ConversationTemplate.");
                continue;
            }

            // A SAY node nested in a variation container is not reachable via GetNodeByGuid, so build
            // the loca key the game uses directly: the conversation's base key (category and path) plus
            // the node guid, which is exactly how the game keys a node's subtitle.
            try
            {
                var key = $"{conv.GetBaseLocaKey()}/{op.NodeGuid}";
                var catData = conv.GetLocaData();
                var entry = catData.HasEntry(key)
                    ? catData.GetEntry(key)
                    : catData.AddEntry(key, null, LocaEntryType.Text, false);
                entry.DefaultTranslation = op.Value;
                entry.Translation = op.Value;
            }
            catch (Exception ex)
            {
                log.Warning($"Locale inject: conversation {op.ConvId} node {op.NodeGuid}: {ex.Message}");
            }
        }

        return allResolved;
    }

    private static bool TryInject(
        object template, string templateId, IReadOnlyList<TemplateDescentStep> descent, string value,
        MelonLogger.Instance log)
    {
        if (!TemplatePatchApplier.TryNavigateDescent(template, descent, out var target, out var navError))
        {
            log.Warning($"Locale inject: {templateId}: {navError}");
            return false;
        }

        var line = (target as Il2CppObjectBase)?.TryCast<BaseLocalizedString>();
        if (line == null)
        {
            log.Warning($"Locale inject: {templateId}: descent target is not a localized string.");
            return false;
        }

        // The live line, for code paths that read the default translation directly.
        try { line.SetDefaultTranslation(value); }
        catch (Exception ex) { log.Warning($"Locale inject: {templateId}: set default failed: {ex.Message}"); }

        // The LocaData entry the UI reads. Key and category come from the live line, so they match
        // exactly what the game builds when it looks the field up.
        try
        {
            var category = line.m_Category;
            var key = $"{category}/{templateId}/{line.m_FieldName}";
            var catData = LocaManager.Get().GetData().GetCategory(category);
            var entry = catData.HasEntry(key)
                ? catData.GetEntry(key)
                : catData.AddEntry(key, null, LocaEntryType.Text, false);
            entry.DefaultTranslation = value;
            entry.Translation = value;
        }
        catch (Exception ex)
        {
            log.Warning($"Locale inject: {templateId}: table write failed: {ex.Message}");
            return false;
        }

        return true;
    }
}
