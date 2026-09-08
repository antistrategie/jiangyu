using Jiangyu.Shared.Bundles;
using Jiangyu.Shared.Templates;
using Jiangyu.Loader.Logging;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Reads compiled <c>templateClones</c> directives out of each loadable mod's
/// jiangyu.json and merges by (templateType, cloneId) so later-loaded mods
/// override earlier ones with a warning. Handed to
/// <see cref="TemplateCloneApplier"/> at runtime.
/// </summary>
internal sealed class TemplateCloneCatalog
{
    private readonly Dictionary<string, Dictionary<string, LoadedCloneDirective>> _clonesByType
        = new(StringComparer.Ordinal);

    // The entry name each spelling files under: one entry per type, named canonically, so a
    // chain declared across a qualified and a short spelling of one type is one chain, and
    // the patch catalogue's entry for the type has the same name.
    private readonly Dictionary<string, string> _entryNames = new(StringComparer.Ordinal);
    private readonly Func<string, string> _canonicalName;
    private readonly Func<string, string, bool> _sameTemplateSpace;

    public TemplateCloneCatalog(Func<string, string> canonicalName = null, Func<string, string, bool> sameTemplateSpace = null)
    {
        _canonicalName = canonicalName ?? TemplateRuntimeAccess.CanonicalTypeName;
        _sameTemplateSpace = sameTemplateSpace ?? TemplateRuntimeAccess.SameTemplateSpace;
    }

    /// <summary>The directive for <paramref name="templateId"/> under <paramref name="templateType"/>
    /// or any entry whose type is an ancestor or descendant of it (a clone is registered in
    /// every ancestor map, so a chain may be declared across such entries), with the entry it
    /// was found in.</summary>
    public bool TryGetDirective(string templateType, string templateId, out LoadedCloneDirective directive, out string entryName)
    {
        directive = null;
        entryName = null;
        if (templateType == null || templateId == null)
            return false;
        var own = EntryNameFor(templateType);
        if (_clonesByType.TryGetValue(own, out var byId) && byId.TryGetValue(templateId, out directive))
        {
            entryName = own;
            return true;
        }

        foreach (var entry in _clonesByType)
        {
            if (!string.Equals(entry.Key, own, StringComparison.Ordinal)
                && _sameTemplateSpace(own, entry.Key)
                && entry.Value.TryGetValue(templateId, out directive))
            {
                entryName = entry.Key;
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the directive for <paramref name="cloneId"/> clones a template that is
    /// itself a clone directive, under the same entry or an alias of it.</summary>
    public bool IsChainedClone(string templateType, string cloneId)
        => TryGetDirective(templateType, cloneId, out var directive, out var entryName)
            && !string.IsNullOrEmpty(directive.SourceId)
            && TryGetDirective(entryName, directive.SourceId, out _, out _);

    /// <summary>The name the catalogue files <paramref name="templateType"/> under
    /// (<see cref="TemplateRuntimeAccess.CanonicalTypeName"/>).</summary>
    public string EntryNameFor(string templateType)
    {
        if (templateType == null)
            return null;
        if (!_entryNames.TryGetValue(templateType, out var name))
            _entryNames[templateType] = name = _canonicalName(templateType) ?? templateType;
        return name;
    }

    public int CloneCount { get; private set; }

    public bool HasClones => _clonesByType.Count > 0;

    public IEnumerable<KeyValuePair<string, Dictionary<string, LoadedCloneDirective>>> EnumerateByType()
        => _clonesByType;

    public void Load(IReadOnlyList<(DiscoveredMod Mod, CompiledTemplatePatchManifest Templates)> mods, LoaderLog log)
    {
        foreach (var (mod, templates) in mods)
        {
            log.Mod = mod.Name;
            LoadFromMod(mod, templates, log);
        }

        log.Mod = null;

        if (_clonesByType.Count == 0)
            return;

        var typeSummaries = _clonesByType
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}: {kv.Value.Count} clone(s)");

        log.Debug(
            $"Loaded {CloneCount} template clone directive(s): {string.Join("; ", typeSummaries)}.");
    }

    private void LoadFromMod(DiscoveredMod mod, CompiledTemplatePatchManifest manifest, LoaderLog log)
    {
        var clones = manifest?.TemplateClones;
        if (clones == null || clones.Count == 0)
            return;

        foreach (var directive in clones)
            TryMergeClone(mod, directive, log);
    }

    private void TryMergeClone(DiscoveredMod mod, CompiledTemplateClone directive, LoaderLog log)
    {
        if (directive == null)
            return;

        var templateType = directive.TemplateType?.Trim();
        if (string.IsNullOrWhiteSpace(templateType))
        {
            log.Warning(
                $"Mod '{mod.Name}': template clone '{directive.SourceId} -> {directive.CloneId}' is missing templateType; skipped.");
            return;
        }

        // An empty sourceId marks a 'create' directive (a fresh template), which
        // is valid; only a clone (sourceId present) needs the differ-from-clone check.
        var isCreate = string.IsNullOrWhiteSpace(directive.SourceId);

        if (string.IsNullOrWhiteSpace(directive.CloneId))
        {
            log.Warning(
                $"Mod '{mod.Name}': template {(isCreate ? "create" : "clone")} on '{templateType}' has empty id; skipped.");
            return;
        }

        if (!isCreate && string.Equals(directive.SourceId, directive.CloneId, StringComparison.Ordinal))
        {
            log.Warning(
                $"Mod '{mod.Name}': template clone '{templateType}:{directive.SourceId}' has cloneId equal to sourceId; skipped.");
            return;
        }

        templateType = EntryNameFor(templateType);
        if (!_clonesByType.TryGetValue(templateType, out var byId))
        {
            byId = new Dictionary<string, LoadedCloneDirective>(StringComparer.Ordinal);
            _clonesByType[templateType] = byId;
        }

        if (byId.TryGetValue(directive.CloneId, out var existing))
        {
            log.Warning(
                $"Override template clone '{templateType}:{directive.CloneId}': "
                + $"later-loaded mod '{mod.Name}' replaces '{existing.OwnerLabel}'.");
            CloneCount--;
        }

        byId[directive.CloneId] = new LoadedCloneDirective(
            templateType, directive.SourceId, directive.CloneId, mod.Name);
        CloneCount++;
    }
}

internal sealed class LoadedCloneDirective
{
    public LoadedCloneDirective(string templateType, string sourceId, string cloneId, string ownerLabel)
    {
        TemplateType = templateType;
        SourceId = sourceId;
        CloneId = cloneId;
        OwnerLabel = ownerLabel;
    }

    public string TemplateType { get; }
    public string SourceId { get; }
    public string CloneId { get; }
    public string OwnerLabel { get; }
}
