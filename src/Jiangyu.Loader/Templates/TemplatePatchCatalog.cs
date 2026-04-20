using System.Text.Json;
using Jiangyu.Shared.Bundles;
using Jiangyu.Shared.Templates;
using MelonLoader;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Reads compiled template patch payloads out of each loadable mod's
/// jiangyu.json, validates against the current slice contract (dotted or
/// indexed member paths, typed scalar/enum/template-reference value), and
/// merges by (templateType, templateId, fieldPath) so later-loaded mods
/// override earlier ones with a warning. The merged catalogue is handed to
/// <see cref="TemplatePatchApplier"/> at runtime.
/// </summary>
internal sealed class TemplatePatchCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Outer key: template type name (e.g. "EntityTemplate").
    // Inner map: templateId -> (fieldPath -> LoadedPatchOperation).
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, LoadedPatchOperation>>> _patches
        = new(StringComparer.Ordinal);

    public int PatchCount { get; private set; }

    public bool HasPatches => _patches.Count > 0;

    public IEnumerable<KeyValuePair<string, Dictionary<string, Dictionary<string, LoadedPatchOperation>>>> EnumerateByType()
        => _patches;

    public void Load(IReadOnlyList<DiscoveredMod> loadableMods, MelonLogger.Instance log)
    {
        foreach (var mod in loadableMods)
            LoadFromMod(mod, log);

        if (_patches.Count == 0)
            return;

        var typeSummaries = _patches
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv =>
            {
                var templatesInType = kv.Value.Count;
                var opsInType = kv.Value.Values.Sum(inner => inner.Count);
                return $"{kv.Key}: {opsInType} op(s) across {templatesInType} template(s)";
            });

        log.Msg(
            $"Loaded {PatchCount} template patch operation(s): {string.Join("; ", typeSummaries)}.");
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
            log.Error($"Mod '{mod.Name}': failed to read template patches: {ex.Message}");
            return;
        }

        var patches = manifest?.TemplatePatches;
        if (patches == null || patches.Count == 0)
            return;

        foreach (var patch in patches)
        {
            var templateType = string.IsNullOrWhiteSpace(patch.TemplateType)
                ? TemplateRuntimeAccess.DefaultTemplateTypeName
                : patch.TemplateType.Trim();

            if (string.IsNullOrWhiteSpace(patch.TemplateId))
            {
                log.Warning(
                    $"Mod '{mod.Name}': template patch skipped ({templateType}: templateId is empty).");
                continue;
            }

            if (patch.Set == null || patch.Set.Count == 0)
            {
                log.Warning(
                    $"Mod '{mod.Name}': template patch for '{templateType}:{patch.TemplateId}' has no 'set' operations.");
                continue;
            }

            foreach (var op in patch.Set)
                TryMergeOperation(mod, templateType, patch.TemplateId, op, log);
        }
    }

    private void TryMergeOperation(
        DiscoveredMod mod, string templateType, string templateId,
        CompiledTemplateSetOperation op, MelonLogger.Instance log)
    {
        if (op == null)
            return;

        if (string.IsNullOrWhiteSpace(op.FieldPath))
        {
            log.Warning(
                $"Mod '{mod.Name}': template patch '{templateType}:{templateId}' has an empty fieldPath.");
            return;
        }

        var effectivePath = op.FieldPath;
        if (!TemplatePatchPathValidator.IsSupportedFieldPath(effectivePath))
        {
            log.Warning(
                $"Mod '{mod.Name}': template patch '{templateType}:{templateId}.{effectivePath}' has unsupported "
                + "path syntax. Supported: dotted names (a.b.c) and indexers (name[N]). Parentheses are rejected.");
            return;
        }

        if (op.Value == null)
        {
            log.Warning(
                $"Mod '{mod.Name}': template patch '{templateType}:{templateId}.{effectivePath}' has no value.");
            return;
        }

        if (!TemplatePatchPathValidator.IsSupportedValue(op.Value))
        {
            log.Warning(
                $"Mod '{mod.Name}': template patch '{templateType}:{templateId}.{effectivePath}' has unsupported or "
                + $"incomplete value (kind={op.Value.Kind}).");
            return;
        }

        if (!_patches.TryGetValue(templateType, out var patchesForType))
        {
            patchesForType = new Dictionary<string, Dictionary<string, LoadedPatchOperation>>(StringComparer.Ordinal);
            _patches[templateType] = patchesForType;
        }

        if (!patchesForType.TryGetValue(templateId, out var operationsForTemplate))
        {
            operationsForTemplate = new Dictionary<string, LoadedPatchOperation>(StringComparer.Ordinal);
            patchesForType[templateId] = operationsForTemplate;
        }

        if (operationsForTemplate.TryGetValue(effectivePath, out var existing))
        {
            log.Warning(
                $"Override template patch '{templateType}:{templateId}.{effectivePath}': "
                + $"later-loaded mod '{mod.Name}' replaces '{existing.OwnerLabel}'.");
            PatchCount--;
        }

        operationsForTemplate[effectivePath] = new LoadedPatchOperation(effectivePath, op.Value, mod.Name);
        PatchCount++;
    }
}

internal sealed class LoadedPatchOperation
{
    public LoadedPatchOperation(string fieldPath, CompiledTemplateValue value, string ownerLabel)
    {
        FieldPath = fieldPath;
        Value = value;
        OwnerLabel = ownerLabel;
    }

    public string FieldPath { get; }
    public CompiledTemplateValue Value { get; }
    public string OwnerLabel { get; }
}
