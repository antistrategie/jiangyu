using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using Jiangyu.Loader.Templates;
using Jiangyu.Shared.Localisation;
using MelonLoader;
using BaseLocalizedString = Il2CppMenace.Tools.BaseLocalizedString;
using LocaData = Il2CppMenace.Tools.LocaData;
using LocaEntryType = Il2CppMenace.Tools.LocaEntryType;
using LocaManager = Il2CppMenace.Tools.LocaManager;

namespace Jiangyu.Loader.Runtime.Localisation;

/// <summary>
/// Gives a clone the translations of the text it inherited but never overrode.
///
/// <para>The game reads a template's text from a <c>LocaData</c> entry keyed
/// <c>&lt;Category&gt;/&lt;templateId&gt;/&lt;FieldName&gt;</c>, and ships one per language for every
/// template it built. A clone is a new id, so it has an entry in no language: cloning copies the
/// source's English <c>m_DefaultTranslation</c> and the lookup falls through to it, leaving the clone
/// permanently English while the template it was cloned from translates normally. Copying the
/// source's entry across to the clone's id fixes that, and asks nothing of a translator, because the
/// text is the source's own and the game already translates it.</para>
///
/// <para>Which fields count as inherited comes from the mod's PATCH OPERATIONS, not from comparing
/// the clone's text against its source's. The compiled patches are read at load, before a single one
/// is applied, so a field the mod authored is known to be authored no matter when this runs.
/// Comparing text cannot say that: the clone only diverges from its source once the patch writing it
/// has landed, and the locale pass can run first, at which point every authored field looks inherited
/// and the mod's own text is buried under the source's translation.</para>
///
/// <para>The text is written onto the clone's own line rather than into the loca table. A clone
/// resolves through a key built from the object name <c>Object.Instantiate</c> gave it, which is its
/// SOURCE's name, so every clone of one source shares that key and a table entry there would serve
/// whichever clone wrote last. The line is per clone, so its default is the one place this text can
/// live and stay right. The source's entry is also copied under the clone's own template id, a key
/// the game never reads but a clone of this clone does.</para>
///
/// <para>Cost matters, because a mod's roster runs to hundreds of clones and resolving a template
/// that does not inherit <c>DataTemplate</c> costs a <c>Resources.FindObjectsOfTypeAll</c> scan. The
/// TYPE is checked first, so a type with no localised property (ConversationTemplate, PerkTreeTemplate
/// and SoundBank between them are most of a voiced mod's clones) is dismissed whole. Only the clone is
/// resolved, never the source: the source contributes an id to a key, nothing more.</para>
/// </summary>
internal static class LocaleInheritance
{
    // The English a clone's line held before this pass first overwrote it, per clone and field.
    // Writing onto the line makes the text outlive the language that put it there, so the source
    // language has to put the original back rather than simply not writing.
    private static readonly Dictionary<(string CloneId, string Member), string> OriginalDefaults = new();

    /// <summary>
    /// Copy each clone's un-overridden text from its source's entry, for the active language, or put
    /// the original English back when the source language is active. <paramref name="decided"/>
    /// holds the clones already handled for the active language and gains the ones this pass
    /// resolves, so a clone is visited once per language however many passes it takes for every
    /// clone to register. Returns how many lines this pass changed.
    /// </summary>
    public static int Apply(
        TemplateCloneCatalog clones, TemplatePatchCatalog patches, MelonLogger.Instance log,
        bool translatable, HashSet<string> decided, Func<string, string, bool> templateHeld = null)
    {
        if (clones == null || !clones.HasClones)
            return 0;

        LocaData data;
        try { data = LocaManager.Get()?.GetData(); }
        catch (Exception ex)
        {
            log.Warning($"Locale inherit: could not read the loca table: {ex.Message}");
            return 0;
        }

        if (data == null)
            return 0;

        var written = 0;
        // A clone reads through its source's entry, and a source declared under another
        // entry of its type family may be decided later in the walk: the entries are walked
        // again until nothing new is decided.
        while (true)
        {
            var decidedBefore = decided.Count;
            written += ApplyOnce(clones, patches, data, log, translatable, decided, templateHeld);
            if (decided.Count == decidedBefore)
                return written;
        }
    }

    private static int ApplyOnce(
        TemplateCloneCatalog clones, TemplatePatchCatalog patches, LocaData data, MelonLogger.Instance log,
        bool translatable, HashSet<string> decided, Func<string, string, bool> templateHeld)
    {
        var written = 0;
        foreach (var typeEntry in clones.EnumerateByType())
        {
            // Resolving a template is the expensive part, so the type answers first: no localised
            // property means nothing on it can be inherited, whatever its clones are.
            var resolvedType = TemplateRuntimeAccess.ResolveTemplateType(typeEntry.Key, out _);
            if (resolvedType == null)
                continue;
            var members = LocalisedMembers(resolvedType);
            if (members.Length == 0)
                continue;

            // A patch may address a clone under any name of its type: the catalogue's aliases.
            var authored = AuthoredMembers(patches?.AliasNames(typeEntry.Key) ?? new[] { typeEntry.Key }, patches);

            // A clone of a clone reads through its source's entry, which this pass writes, so the
            // source has to be done first. Same ordering the clone applier uses: anything whose
            // source is not a sibling clone is already available.
            var byId = typeEntry.Value;
            var ordered = TemplateCloneApplier.OrderBySourceAvailability(
                byId.Values, id => !byId.ContainsKey(id), out _);

            foreach (var directive in ordered)
            {
                if (string.IsNullOrEmpty(directive.SourceId) || decided.Contains(LateTemplateSet.Key(typeEntry.Key, directive.CloneId)))
                    continue;
                // A clone with a block of patches still held is decided on the pass that
                // follows the block: its authored text has not been written yet. So is a
                // clone whose source is held or not yet decided, since it reads through the
                // source's entry.
                if (templateHeld != null
                    && (templateHeld(typeEntry.Key, directive.CloneId) || templateHeld(typeEntry.Key, directive.SourceId)))
                    continue;
                // A source that is itself a clone, under this entry or an alias of it, reads
                // through its own entry, written when it was decided. A created source has no
                // entry to wait for.
                if (clones.TryGetDirective(typeEntry.Key, directive.SourceId, out var sourceDirective, out var sourceEntry)
                    && !string.IsNullOrEmpty(sourceDirective.SourceId)
                    && !decided.Contains(LateTemplateSet.Key(sourceEntry, directive.SourceId)))
                    continue;
                if (!MirrorOne(directive, resolvedType, members, authored, data, translatable, log, out var mirrored))
                    continue;   // not registered yet; a later pass looks again
                decided.Add(LateTemplateSet.Key(typeEntry.Key, directive.CloneId));
                written += mirrored;
            }
        }

        return written;
    }

    // False when the clone is not live yet. True once every member has been decided, whether or
    // not any was written.
    private static bool MirrorOne(
        LoadedCloneDirective directive,
        Type resolvedType,
        PropertyInfo[] members,
        IReadOnlyDictionary<string, Dictionary<string, string>> authored,
        LocaData data,
        bool translatable,
        MelonLogger.Instance log,
        out int written)
    {
        written = 0;
        if (!TemplateRuntimeAccess.TryGetTemplateById(
                resolvedType, directive.CloneId, out var cloneWrapper, out _)
            || !Il2CppReflectiveCast.TryCast(cloneWrapper, resolvedType, out var clone, out _))
            return false;

        authored.TryGetValue(directive.CloneId, out var authoredHere);

        foreach (var member in members)
        {
            if (authoredHere != null && authoredHere.ContainsKey(member.Name))
                continue;   // the mod wrote this field, so it is the mod's to translate

            var line = ReadLine(clone, member);
            if (line == null)
                continue;

            try
            {
                if (!translatable)
                {
                    // Back to the English the clone started with, for the fields this pass changed.
                    if (OriginalDefaults.TryGetValue((directive.CloneId, member.Name), out var english))
                    {
                        line.SetDefaultTranslation(english);
                        written++;
                    }
                    continue;
                }

                var category = line.m_Category;
                var fieldName = line.m_FieldName;
                if (string.IsNullOrEmpty(fieldName))
                    continue;

                var categoryData = data.GetCategory(category);
                if (categoryData == null)
                    continue;

                var sourceKey = $"{category}/{directive.SourceId}/{fieldName}";
                if (!categoryData.HasEntry(sourceKey))
                    continue;   // the game ships no text for this field on the source either

                var sourceEntry = categoryData.GetEntry(sourceKey);
                var translated = sourceEntry.Translation;
                if (string.IsNullOrEmpty(translated))
                    continue;

                // Written onto the line, not into the entry the game reads. A clone resolves through
                // a key built from the object name Object.Instantiate gave it, which is its SOURCE's
                // name, so every clone of one source shares that key and a table entry there would
                // serve whichever clone wrote last. The line is per clone, so its default is the one
                // place this text can live and stay right. It is the clone's own line, never the
                // source's.
                // The English kept for the source language is the clone's own line before this
                // pass first wrote it, unless the line was just copied from a source already
                // translated: by this pass, in which case the source's recorded English is the
                // original, or by the mod's own translation of a field it authored on the
                // source, in which case the authored English is.
                var key = (directive.CloneId, member.Name);
                if (!OriginalDefaults.ContainsKey(key))
                {
                    OriginalDefaults[key] = OriginalDefaults.TryGetValue((directive.SourceId, member.Name), out var sourceEnglish)
                        ? sourceEnglish
                        : authored.TryGetValue(directive.SourceId, out var sourceAuthored)
                            && sourceAuthored.TryGetValue(member.Name, out var authoredEnglish)
                            && authoredEnglish != null
                            ? authoredEnglish
                            : line.m_DefaultTranslation;
                }

                line.SetDefaultTranslation(translated);

                // The source's entry copied under the clone's own id as well. The game never looks
                // this key up, but a clone of this clone does: its source key is this clone's id,
                // and the clones are ordered so the source is written first.
                var cloneKey = $"{category}/{directive.CloneId}/{fieldName}";
                var cloneEntry = categoryData.HasEntry(cloneKey)
                    ? categoryData.GetEntry(cloneKey)
                    : categoryData.AddEntry(cloneKey, null, LocaEntryType.Text, false);
                cloneEntry.DefaultTranslation = sourceEntry.DefaultTranslation;
                cloneEntry.Translation = translated;
                written++;
            }
            catch (Exception ex)
            {
                log.Warning($"Locale inherit: {directive.CloneId}.{member.Name}: {ex.Message}");
            }
        }

        return true;
    }

    // Per template id, the top-level members the mod writes localised text into. Two shapes reach a
    // line: an edit descending into the member (`set "Description" { set "m_DefaultTranslation" }`)
    // and a replacement built at the member (`set "Description" type="LocalizedMultiLine" { ... }`).
    // Anything deeper than one step is a line nested inside a member rather than the member itself,
    // and no such line is one of this template's own localised properties.
    // Ops addressed under the type or any of its ancestor names all write the same clone.
    // The value is the English the mod wrote, the last written when several ops write the
    // member: a clone rebased from that member's owner takes it as its own original.
    private static Dictionary<string, Dictionary<string, string>> AuthoredMembers(
        IReadOnlyList<string> typeNames, TemplatePatchCatalog patches)
    {
        var byTemplate = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (patches == null)
            return byTemplate;

        foreach (var typeEntry in patches.EnumerateByType())
        {
            if (!typeNames.Contains(typeEntry.Key))
                continue;

            foreach (var template in typeEntry.Value)
            {
                foreach (var op in template.Value)
                {
                    var member = AuthoredMember(op);
                    if (member == null)
                        continue;
                    if (!byTemplate.TryGetValue(template.Key, out var members))
                        byTemplate[template.Key] = members = new Dictionary<string, string>(StringComparer.Ordinal);
                    members[member] = AuthoredText(op) ?? (members.TryGetValue(member, out var earlier) ? earlier : null);
                }
            }
        }

        return byTemplate;
    }

    // The English an op writes into a member's line: the value of a descent into
    // m_DefaultTranslation, or of the m_DefaultTranslation set inside a replacement built at
    // the member.
    private static string AuthoredText(LoadedPatchOperation op)
    {
        if (op.FieldPath == LocaleCoordinate.DefaultTranslationMember)
            return op.Value?.String;
        var construction = op.Value?.TypeConstruction ?? op.Value?.Composite;
        if (construction?.Operations == null)
            return null;
        string text = null;
        foreach (var inner in construction.Operations)
        {
            if (inner.Op == Jiangyu.Shared.Templates.CompiledTemplateOp.Set
                && inner.FieldPath == LocaleCoordinate.DefaultTranslationMember
                && inner.Value?.String != null)
                text = inner.Value.String;
        }

        return text;
    }

    private static string AuthoredMember(LoadedPatchOperation op)
    {
        if (op.Op != Jiangyu.Shared.Templates.CompiledTemplateOp.Set)
            return null;

        if (op.FieldPath == LocaleCoordinate.DefaultTranslationMember)
            return op.Descent is { Count: 1 } ? op.Descent[0].Field : null;

        return CarriesLocalisedText(op.Value) && (op.Descent == null || op.Descent.Count == 0)
            ? op.FieldPath
            : null;
    }

    private static bool CarriesLocalisedText(Jiangyu.Shared.Templates.CompiledTemplateValue value)
    {
        var composite = value?.Composite ?? value?.TypeConstruction;
        if (composite == null)
            return false;
        foreach (var inner in composite.Operations)
            if (inner.FieldPath == LocaleCoordinate.DefaultTranslationMember)
                return true;
        return false;
    }

    private static BaseLocalizedString ReadLine(object instance, PropertyInfo member)
    {
        try { return (member.GetValue(instance) as Il2CppObjectBase)?.TryCast<BaseLocalizedString>(); }
        catch { return null; }
    }

    // The template's localised members, selected by declared type so no unrelated property is ever
    // evaluated. Cached per type: a mod's clones cluster on a handful of template types.
    private static readonly Dictionary<Type, PropertyInfo[]> MembersByType = new();

    private static PropertyInfo[] LocalisedMembers(Type type)
    {
        if (MembersByType.TryGetValue(type, out var cached))
            return cached;

        PropertyInfo[] members;
        try
        {
            members = type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(p => p.CanRead
                    && p.GetIndexParameters().Length == 0
                    && typeof(BaseLocalizedString).IsAssignableFrom(p.PropertyType))
                .ToArray();
        }
        catch
        {
            members = Array.Empty<PropertyInfo>();
        }

        MembersByType[type] = members;
        return members;
    }
}
