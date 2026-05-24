using AssetRipper.Assets;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_213;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_83;
using AssetRipper.SourceGenerated.Extensions;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Assets;

/// <summary>
/// Walks every collection in a loaded <see cref="GameData"/> and produces
/// the searchable <see cref="AssetIndex"/> the rest of the pipeline consumes:
/// one <see cref="AssetEntry"/> per asset with class identity, plus typed
/// sub-objects for sprite/audio/soundbank/conversation metadata. Tagged
/// discriminators sampled from vanilla ConversationTemplates are surfaced
/// on <see cref="AssetIndex.TaggedDiscriminators"/> so compile-time
/// validation only accepts forms vanilla emits.
///
/// <para>Pure transformation: takes pre-loaded game data, returns the
/// in-memory index. Cache I/O lives on <see cref="AssetPipelineService"/>.</para>
/// </summary>
internal static class AssetIndexBuilder
{
    public static AssetIndex Build(GameData gameData)
    {
        var entries = new List<AssetEntry>();
        // Discriminator sample accumulators keyed by polymorphic-base FQN.
        // Sets give natural dedup; converted to sorted lists at the end.
        var nodeDiscriminators = new HashSet<string>(StringComparer.Ordinal);
        var requirementDiscriminators = new HashSet<string>(StringComparer.Ordinal);
        var actionDiscriminators = new HashSet<string>(StringComparer.Ordinal);

        foreach (var collection in gameData.GameBundle.FetchAssetCollections())
        {
            string collectionName = collection.Name;

            foreach (IUnityObjectBase asset in collection)
            {
                var entry = new AssetEntry
                {
                    Name = asset.GetBestName(),
                    CanonicalPath = AssetPipelineService.BuildCanonicalAssetPath(collectionName, asset.ClassName, asset.GetBestName(), asset.PathID),
                    ClassName = asset.ClassName,
                    ClassId = asset.ClassID,
                    PathId = asset.PathID,
                    Collection = collectionName,
                };

                if (asset is ISprite sprite)
                {
                    var spriteMeta = new AssetSpriteMetadata();
                    var backing = AssetPipelineService.ResolveSpriteBackingTexture(sprite);
                    if (backing is not null)
                    {
                        spriteMeta.BackingTexturePathId = backing.PathID;
                        spriteMeta.BackingTextureCollection = backing.Collection.Name;
                        spriteMeta.BackingTextureName = backing.GetBestName();
                    }

                    AssetPipelineService.PopulateSpriteAtlasMetadata(sprite, spriteMeta);
                    entry.Sprite = spriteMeta;
                }
                else if (asset is IAudioClip audioClip)
                {
                    var audioMeta = new AssetAudioMetadata();
                    if (audioClip.Has_Frequency())
                        audioMeta.Frequency = audioClip.Frequency;
                    if (audioClip.Has_Channels())
                        audioMeta.Channels = audioClip.Channels;
                    entry.Audio = audioMeta;
                }
                else if (asset is IMonoBehaviour monoBehaviour)
                {
                    if (entry.Name is { } entryName && IsSoundBankAssetName(entryName))
                    {
                        // Inspect once and reuse for both NamedChildren (sound
                        // names) and BankId extraction. Saves a second walk
                        // through the same m_Structure tree.
                        var inspection = TryInspectSoundBank(monoBehaviour, gameData);
                        if (inspection is not null)
                        {
                            entry.SoundBank = new AssetSoundBankMetadata
                            {
                                NamedChildren = ExtractSoundBankSoundNames(inspection),
                                BankId = ExtractSoundBankBankId(inspection),
                            };
                        }
                    }
                    else if (IsConversationTemplate(monoBehaviour))
                    {
                        var inspection = TryInspectConversationTemplate(monoBehaviour, gameData);
                        if (inspection is not null)
                        {
                            entry.Conversation = new AssetConversationMetadata
                            {
                                Roles = ExtractConversationRoles(inspection),
                                Path = ExtractConversationPath(inspection),
                            };
                            ExtractTaggedDiscriminators(
                                inspection,
                                nodeDiscriminators,
                                requirementDiscriminators,
                                actionDiscriminators);
                        }
                    }
                }

                entries.Add(entry);
            }
        }

        var discriminators = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (nodeDiscriminators.Count > 0)
            discriminators["Il2CppMenace.Conversations.BaseConversationNode"] = nodeDiscriminators.OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (requirementDiscriminators.Count > 0)
            discriminators["Il2CppMenace.Conversations.BaseRoleRequirement"] = requirementDiscriminators.OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (actionDiscriminators.Count > 0)
            discriminators["Il2CppMenace.Conversations.BaseConversationNodeAction"] = actionDiscriminators.OrderBy(s => s, StringComparer.Ordinal).ToList();

        return new AssetIndex
        {
            Assets = entries,
            TaggedDiscriminators = discriminators.Count > 0 ? discriminators : null,
        };
    }

    /// <summary>
    /// Sample TYPE prefixes from a ConversationTemplate's tagged-string
    /// fields. Three sources:
    /// <list type="bullet">
    /// <item>Top-level <c>m_Structure.Nodes.m_SerializedNodes</c> entries
    /// → <c>BaseConversationNode</c> discriminators (ACTION, SAY,
    /// VARIATION, ...).</item>
    /// <item>Per-role <c>m_Structure.Roles[*].m_SerializedRequirements</c>
    /// entries → <c>BaseRoleRequirement</c> discriminators (HasOneTag,
    /// IsOnBattlefield, ...).</item>
    /// <item>Per-ACTION-node <c>m_SerAction</c> nested JSON →
    /// <c>BaseConversationNodeAction</c> discriminators (SetFlag, ...).
    /// </item>
    /// </list>
    /// Strings are <c>"TYPE|{json}"</c>; the prefix is everything before
    /// the first <c>|</c>. Inner branches inside VARIATION/CHOICE bodies
    /// would need recursive JSON parsing through nested
    /// <c>m_SerializedNodes</c> arrays inside the outer JSON, which
    /// adds cost and complexity for diminishing returns (every
    /// discriminator type appears as a top-level node somewhere in
    /// vanilla data). Treated as out-of-scope for v1.
    /// </summary>
    private static void ExtractTaggedDiscriminators(
        ObjectFieldInspection inspection,
        ISet<string> nodeOut,
        ISet<string> requirementOut,
        ISet<string> actionOut)
    {
        var structure = inspection.Fields.FirstOrDefault(f =>
            string.Equals(f.Name, "m_Structure", StringComparison.Ordinal));
        if (structure?.Fields is null) return;

        var nodes = structure.Fields.FirstOrDefault(f =>
            string.Equals(f.Name, "Nodes", StringComparison.Ordinal));
        var serialisedNodes = nodes?.Fields?.FirstOrDefault(f =>
            string.Equals(f.Name, "m_SerializedNodes", StringComparison.Ordinal));
        if (serialisedNodes?.Elements is { Count: > 0 })
        {
            foreach (var element in serialisedNodes.Elements)
            {
                if (element.Value?.ToString() is not { } entry) continue;
                if (TryExtractDiscriminator(entry, out var disc))
                {
                    nodeOut.Add(disc);
                    // ACTION nodes carry an inner "m_SerAction" field
                    // holding a TYPE|json string for the polymorphic
                    // action subtype. Surface it as a separate base.
                    if (string.Equals(disc, "ACTION", StringComparison.Ordinal))
                        ExtractActionDiscriminator(entry, actionOut);
                }
            }
        }

        var roles = structure.Fields.FirstOrDefault(f =>
            string.Equals(f.Name, "Roles", StringComparison.Ordinal));
        if (roles?.Elements is { Count: > 0 })
        {
            foreach (var role in roles.Elements)
            {
                var requirements = role.Fields?.FirstOrDefault(f =>
                    string.Equals(f.Name, "m_SerializedRequirements", StringComparison.Ordinal));
                if (requirements?.Elements is null) continue;
                foreach (var element in requirements.Elements)
                {
                    if (element.Value?.ToString() is not { } entry) continue;
                    if (TryExtractDiscriminator(entry, out var disc))
                        requirementOut.Add(disc);
                }
            }
        }
    }

    private static bool TryExtractDiscriminator(string entry, out string discriminator)
    {
        var pipe = entry.IndexOf('|');
        if (pipe <= 0)
        {
            discriminator = string.Empty;
            return false;
        }
        discriminator = entry[..pipe];
        return discriminator.Length > 0;
    }

    /// <summary>
    /// Parse an <c>ACTION|{...}</c> entry's inner JSON to extract the
    /// <c>m_SerAction</c> value's TYPE prefix. The inner field is
    /// itself a tagged string with escaped quotes inside the outer
    /// JSON; a single <c>JsonDocument.Parse</c> pass unwraps both.
    /// </summary>
    private static void ExtractActionDiscriminator(string actionEntry, ISet<string> actionOut)
    {
        var pipe = actionEntry.IndexOf('|');
        if (pipe < 0 || pipe >= actionEntry.Length - 1) return;
        var json = actionEntry[(pipe + 1)..];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("m_SerAction", out var serAction)) return;
            if (serAction.ValueKind != System.Text.Json.JsonValueKind.String) return;
            var inner = serAction.GetString();
            if (string.IsNullOrEmpty(inner)) return;
            if (TryExtractDiscriminator(inner, out var disc))
                actionOut.Add(disc);
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed inner JSON is non-fatal for sampling. Skip and
            // continue; missing one discriminator from one vanilla node
            // doesn't break compile or playback.
        }
    }

    /// <summary>
    /// True if <paramref name="assetName"/> matches one of MENACE's
    /// <c>Stem.SoundBank</c> asset naming patterns. Covers the 15 generic
    /// <c>*_soundbank</c> banks (weapons, UI, environment, etc.) and the
    /// 29 character-specific bark banks under
    /// <c>tactical_barks_*</c> / <c>strategic_barks_*</c>. A robust
    /// alternative is to follow the MonoBehaviour's script reference, but
    /// the name-pattern check is cheap and stable across builds.
    /// </summary>
    private static bool IsSoundBankAssetName(string assetName)
        => assetName.EndsWith("_soundbank", StringComparison.Ordinal)
           || assetName.StartsWith("tactical_barks_", StringComparison.Ordinal)
           || assetName.StartsWith("strategic_barks_", StringComparison.Ordinal);

    /// <summary>
    /// Walks a Stem.SoundBank asset and returns its inspected field tree, or
    /// null if inspection fails. Shared by per-bank extractors so each bank
    /// only goes through ObjectFieldInspector once.
    /// </summary>
    private static ObjectFieldInspection? TryInspectSoundBank(
        IMonoBehaviour monoBehaviour,
        GameData gameData)
    {
        try
        {
            // Depth 4 reaches m_Structure -> sounds -> element -> name.
            // ArraySample 4096 covers any bank without truncation.
            var inspection = ObjectFieldInspector.Inspect(monoBehaviour, maxDepth: 4, maxArraySampleLength: 4096);
            ManagedTypeInspectionEnricher.Enrich(monoBehaviour, gameData.AssemblyManager, inspection.Fields);
            OdinPayloadEnricher.Enrich(inspection.Fields);
            return inspection;
        }
        catch
        {
            // Per-bank inspection failure is non-fatal: a missing
            // NamedChildren list and BankId just falls back to empty
            // autocomplete + no bank-name resolution for that one bank,
            // rather than failing the whole index build.
            return null;
        }
    }

    /// <summary>Returns the names of <c>m_Structure.sounds[].name</c> for the
    /// already-inspected SoundBank, or null when there are no sounds.</summary>
    private static List<string>? ExtractSoundBankSoundNames(ObjectFieldInspection inspection)
    {
        var structure = inspection.Fields.FirstOrDefault(f =>
            string.Equals(f.Name, "m_Structure", StringComparison.Ordinal));
        var sounds = structure?.Fields?.FirstOrDefault(f =>
            string.Equals(f.Name, "sounds", StringComparison.Ordinal));
        if (sounds?.Elements is null || sounds.Elements.Count == 0)
            return null;

        var names = new List<string>(sounds.Elements.Count);
        foreach (var element in sounds.Elements)
        {
            if (element.Fields is null) continue;
            var nameField = element.Fields.FirstOrDefault(f =>
                string.Equals(f.Name, "name", StringComparison.Ordinal));
            var value = nameField?.Value?.ToString();
            if (!string.IsNullOrEmpty(value))
                names.Add(value);
        }
        return names.Count > 0 ? names : null;
    }

    /// <summary>Returns the int value of <c>m_Structure.bankId</c> for the
    /// already-inspected SoundBank, or null if the field is missing or
    /// unparseable.</summary>
    private static int? ExtractSoundBankBankId(ObjectFieldInspection inspection)
    {
        var structure = inspection.Fields.FirstOrDefault(f =>
            string.Equals(f.Name, "m_Structure", StringComparison.Ordinal));
        var bankIdField = structure?.Fields?.FirstOrDefault(f =>
            string.Equals(f.Name, "bankId", StringComparison.Ordinal));
        if (bankIdField?.Value is null) return null;
        return bankIdField.Value switch
        {
            int i => i,
            long l => unchecked((int)l),
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// True when the MonoBehaviour's script reference resolves to the
    /// <c>ConversationTemplate</c> class. Used to gate per-asset Roles
    /// extraction since ConversationTemplate asset names are non-unique
    /// (every speaker has its own <c>click_bark</c> etc.).
    /// </summary>
    private static bool IsConversationTemplate(IMonoBehaviour monoBehaviour)
    {
        if (!monoBehaviour.TryGetScript(out var script) || script is null)
            return false;
        return string.Equals(script.ClassName_R.String, "ConversationTemplate", StringComparison.Ordinal);
    }

    /// <summary>
    /// Walks a ConversationTemplate asset and returns its inspected field
    /// tree, or null if inspection fails. Mirrors
    /// <see cref="TryInspectSoundBank"/>: per-asset inspection failure is
    /// non-fatal so a single malformed asset doesn't abort the index.
    /// </summary>
    private static ObjectFieldInspection? TryInspectConversationTemplate(
        IMonoBehaviour monoBehaviour,
        GameData gameData)
    {
        try
        {
            // Depth 5 reaches both
            //   m_Structure -> Roles -> element -> RoleName/Guid (depth 4
            //     fields on a depth-3 element) for role extraction, and
            //   m_Structure -> Roles -> element -> m_SerializedRequirements
            //     -> string element (depth 5) for discriminator sampling.
            // ArraySample 64 covers any plausible conversation.
            var inspection = ObjectFieldInspector.Inspect(monoBehaviour, maxDepth: 5, maxArraySampleLength: 64);
            ManagedTypeInspectionEnricher.Enrich(monoBehaviour, gameData.AssemblyManager, inspection.Fields);
            OdinPayloadEnricher.Enrich(inspection.Fields);
            return inspection;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the string value of <c>m_Structure.Path</c> for an
    /// inspected ConversationTemplate. Path is the unique identifier
    /// (asset names like <c>click_bark</c> are shared across many
    /// speaker conversations).
    /// </summary>
    private static string? ExtractConversationPath(ObjectFieldInspection inspection)
    {
        var structure = inspection.Fields.FirstOrDefault(f =>
            string.Equals(f.Name, "m_Structure", StringComparison.Ordinal));
        var pathField = structure?.Fields?.FirstOrDefault(f =>
            string.Equals(f.Name, "Path", StringComparison.Ordinal));
        var value = pathField?.Value?.ToString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Returns <c>(RoleName, Guid)</c> pairs for each entry in
    /// <c>m_Structure.Roles</c>, or null if the conversation has no roles.
    /// </summary>
    private static List<AssetEntryRole>? ExtractConversationRoles(ObjectFieldInspection inspection)
    {
        var structure = inspection.Fields.FirstOrDefault(f =>
            string.Equals(f.Name, "m_Structure", StringComparison.Ordinal));
        var roles = structure?.Fields?.FirstOrDefault(f =>
            string.Equals(f.Name, "Roles", StringComparison.Ordinal));
        if (roles?.Elements is null || roles.Elements.Count == 0)
            return null;

        var result = new List<AssetEntryRole>(roles.Elements.Count);
        foreach (var element in roles.Elements)
        {
            if (element.Fields is null) continue;
            var nameField = element.Fields.FirstOrDefault(f =>
                string.Equals(f.Name, "RoleName", StringComparison.Ordinal));
            var guidField = element.Fields.FirstOrDefault(f =>
                string.Equals(f.Name, "Guid", StringComparison.Ordinal));
            var name = nameField?.Value?.ToString();
            if (string.IsNullOrEmpty(name) || guidField?.Value is null) continue;

            int guid = guidField.Value switch
            {
                int i => i,
                long l => unchecked((int)l),
                string s when int.TryParse(s, out var parsed) => parsed,
                _ => 0,
            };
            if (guid == 0) continue;

            result.Add(new AssetEntryRole { Name = name, Guid = guid });
        }
        return result.Count == 0 ? null : result;
    }
}
