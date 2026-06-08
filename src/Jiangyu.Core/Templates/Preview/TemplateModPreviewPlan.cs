using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Compile;
using Jiangyu.Core.Models;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Templates;

public readonly record struct TemplatePreviewKey(string TemplateType, string TemplateId);

public sealed class TemplateModPreviewPlan
{
    private readonly Dictionary<TemplatePreviewKey, CompiledTemplateClone> _clones;
    private readonly ILookup<TemplatePreviewKey, CompiledTemplatePatch> _patches;

    private TemplateModPreviewPlan(
        Dictionary<TemplatePreviewKey, CompiledTemplateClone> clones,
        ILookup<TemplatePreviewKey, CompiledTemplatePatch> patches)
    {
        _clones = clones;
        _patches = patches;
    }

    public static TemplateModPreviewPlan Load(string modPath, ILogSink log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modPath);
        ArgumentNullException.ThrowIfNull(log);

        string manifestPath = ResolveManifestPath(modPath);
        var modDir = Path.GetDirectoryName(manifestPath)!;
        var templates = CompiledTemplatePatchManifest.TryLoad(modDir) ?? new CompiledTemplatePatchManifest();

        var cloneResult = TemplatePatchEmitter.EmitClones(templates.TemplateClones, log);
        var patchResult = TemplatePatchEmitter.Emit(templates.TemplatePatches, log);

        if (!cloneResult.Success || !patchResult.Success)
        {
            throw new InvalidOperationException(
                $"Mod preview cannot continue because '{manifestPath}' has invalid template clone or patch entries.");
        }

        var clones = (cloneResult.Clones ?? [])
            .ToDictionary(
                clone => new TemplatePreviewKey(clone.TemplateType!, clone.CloneId),
                clone => clone);

        var patches = (patchResult.Patches ?? [])
            .ToLookup(
                patch => new TemplatePreviewKey(NormaliseTemplateType(patch.TemplateType), patch.TemplateId));

        return new TemplateModPreviewPlan(clones, patches);
    }

    public bool TryGetClone(TemplatePreviewKey key, out CompiledTemplateClone clone)
        => _clones.TryGetValue(key, out clone!);

    public IReadOnlyList<CompiledTemplatePatch> GetPatches(TemplatePreviewKey key)
        => [.. _patches[key]];

    public TemplatePreviewKey ResolveUltimateSource(TemplatePreviewKey key)
    {
        var seen = new HashSet<TemplatePreviewKey>();
        var current = key;

        while (_clones.TryGetValue(current, out CompiledTemplateClone? clone))
        {
            // A 'create' directive (no source) is its own ultimate source: it
            // instantiates a fresh template rather than copying an existing one.
            if (clone.SourceId is null)
                break;

            if (!seen.Add(current))
            {
                throw new InvalidOperationException(
                    $"Template clone preview found a cycle while resolving '{key.TemplateType}:{key.TemplateId}'.");
            }

            current = new TemplatePreviewKey(clone.TemplateType!, clone.SourceId);
        }

        return current;
    }

    public static string ResolveManifestPath(string modPath)
    {
        if (Directory.Exists(modPath))
        {
            string manifestPath = Path.Combine(modPath, ModManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException($"No {ModManifest.FileName} found in '{modPath}'.", manifestPath);
            }

            return manifestPath;
        }

        if (File.Exists(modPath))
        {
            return modPath;
        }

        throw new FileNotFoundException($"Mod path does not exist: {modPath}", modPath);
    }

    private static string NormaliseTemplateType(string? templateType)
        => string.IsNullOrWhiteSpace(templateType) ? "EntityTemplate" : templateType.Trim();
}
