using System.Text.Json;
using Jiangyu.Shared.Bundles;
using Jiangyu.Shared.Templates;
using MelonLoader;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Reads compiled <c>templateClones</c> directives out of each loadable mod's
/// jiangyu.json and merges by (templateType, cloneId) so later-loaded mods
/// override earlier ones with a warning. Handed to
/// <see cref="TemplateCloneApplier"/> at runtime.
/// </summary>
internal sealed class TemplateCloneCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, Dictionary<string, LoadedCloneDirective>> _clonesByType
        = new(StringComparer.Ordinal);

    public int CloneCount { get; private set; }

    public bool HasClones => _clonesByType.Count > 0;

    public IEnumerable<KeyValuePair<string, Dictionary<string, LoadedCloneDirective>>> EnumerateByType()
        => _clonesByType;

    public void Load(IReadOnlyList<DiscoveredMod> loadableMods, MelonLogger.Instance log)
    {
        foreach (var mod in loadableMods)
            LoadFromMod(mod, log);

        if (_clonesByType.Count == 0)
            return;

        var typeSummaries = _clonesByType
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}: {kv.Value.Count} clone(s)");

        log.Msg(
            $"Loaded {CloneCount} template clone directive(s): {string.Join("; ", typeSummaries)}.");
    }

    private void LoadFromMod(DiscoveredMod mod, MelonLogger.Instance log)
    {
        if (string.IsNullOrEmpty(mod.ManifestPath) || !File.Exists(mod.ManifestPath))
            return;

        CompiledTemplatePatchManifest manifest;
        try
        {
            var json = File.ReadAllText(mod.ManifestPath);
            manifest = JsonSerializer.Deserialize<CompiledTemplatePatchManifest>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            log.Error($"Mod '{mod.Name}': failed to read template clones: {ex.Message}");
            return;
        }

        var clones = manifest?.TemplateClones;
        if (clones == null || clones.Count == 0)
            return;

        foreach (var directive in clones)
            TryMergeClone(mod, directive, log);
    }

    private void TryMergeClone(DiscoveredMod mod, CompiledTemplateClone directive, MelonLogger.Instance log)
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

        if (string.IsNullOrWhiteSpace(directive.SourceId))
        {
            log.Warning(
                $"Mod '{mod.Name}': template clone on '{templateType}' has empty sourceId; skipped.");
            return;
        }

        if (string.IsNullOrWhiteSpace(directive.CloneId))
        {
            log.Warning(
                $"Mod '{mod.Name}': template clone on '{templateType}' has empty cloneId; skipped.");
            return;
        }

        if (string.Equals(directive.SourceId, directive.CloneId, StringComparison.Ordinal))
        {
            log.Warning(
                $"Mod '{mod.Name}': template clone '{templateType}:{directive.SourceId}' has cloneId equal to sourceId; skipped.");
            return;
        }

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
