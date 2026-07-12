using Jiangyu.Shared.Bundles;
using Jiangyu.Shared.Templates;
using Jiangyu.Loader.Logging;

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
    // Outer key: template type name (e.g. "EntityTemplate").
    // Middle key: templateId. Inner list: operations in applied order. Set
    // ops dedup on fieldPath within the inner list (later replaces earlier);
    // Append ops always add a new entry so N appends on the same field apply
    // N new elements in authored/load order.
    private readonly Dictionary<string, Dictionary<string, List<LoadedPatchOperation>>> _patches
        = new(StringComparer.Ordinal);

    public int PatchCount { get; private set; }

    public bool HasPatches => _patches.Count > 0;

    public IEnumerable<KeyValuePair<string, Dictionary<string, List<LoadedPatchOperation>>>> EnumerateByType()
        => _patches;

    /// <summary>
    /// The compiled patch ops for one template, in applied order, or false when
    /// none exist. The clone applier replays these onto a chained clone after
    /// rebuilding it from its patched source, so the clone's own appends/sets
    /// land on top of the inherited fields.
    /// </summary>
    public bool TryGetOperations(string templateType, string templateId, out List<LoadedPatchOperation> ops)
    {
        ops = null;
        return templateType != null && templateId != null
            && _patches.TryGetValue(templateType, out var byId)
            && byId.TryGetValue(templateId, out ops);
    }

    /// <summary>
    /// The set of top-level member names the patch ops for
    /// <paramref name="templateId"/> write to. The clone applier uses this to
    /// tell which non-collection fields a clone authored itself, so
    /// re-inheritance from a cloned source fills only the ones the clone left
    /// untouched (never overwriting an authored value, nor sharing a mutable
    /// sub-object the clone will patch). A nested op (descent into a field, or a
    /// dotted/indexed path) counts against its outermost member.
    /// </summary>
    public HashSet<string> TouchedTopLevelFields(string templateType, string templateId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (TryGetOperations(templateType, templateId, out var ops))
            foreach (var op in ops)
            {
                var top = TopLevelField(op);
                if (!string.IsNullOrEmpty(top))
                    result.Add(top);
            }
        return result;
    }

    // The outermost member an op targets: the first descent step's Field when
    // the op descends, otherwise the first segment of the inner FieldPath
    // (before any '.' or '['). Internal so the loader tests can exercise the
    // extraction directly (InternalsVisibleTo).
    internal static string TopLevelField(LoadedPatchOperation op)
    {
        if (op.Descent is { Count: > 0 })
            return op.Descent[0].Field;
        var path = op.FieldPath;
        if (string.IsNullOrEmpty(path))
            return null;
        var cut = path.IndexOfAny(new[] { '.', '[' });
        return cut < 0 ? path : path.Substring(0, cut);
    }

    public void Load(IReadOnlyList<(DiscoveredMod Mod, CompiledTemplatePatchManifest Templates)> mods, LoaderLog log)
    {
        foreach (var (mod, templates) in mods)
        {
            log.Mod = mod.Name;
            LoadFromMod(mod, templates, log);
        }

        log.Mod = null;

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

    private void LoadFromMod(DiscoveredMod mod, CompiledTemplatePatchManifest manifest, LoaderLog log)
    {
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
        CompiledTemplateSetOperation op, LoaderLog log)
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

        var opForValidation = new CompiledTemplateSetOperation
        {
            Op = op.Op,
            FieldPath = effectivePath,
            Index = op.Index,
            Value = op.Value,
        };
        if (!TemplatePatchPathValidator.TryValidateOpShape(opForValidation, effectivePath, out var opShapeError))
        {
            log.Warning(
                $"Mod '{mod.Name}': template patch '{templateType}:{templateId}.{effectivePath}' — {opShapeError}");
            return;
        }

        if (!_patches.TryGetValue(templateType, out var patchesForType))
        {
            patchesForType = new Dictionary<string, List<LoadedPatchOperation>>(StringComparer.Ordinal);
            _patches[templateType] = patchesForType;
        }

        if (!patchesForType.TryGetValue(templateId, out var operationsForTemplate))
        {
            operationsForTemplate = new List<LoadedPatchOperation>();
            patchesForType[templateId] = operationsForTemplate;
        }

        // Set ops dedup by destination — later replaces earlier, whether
        // from the same mod or a later-loaded mod. The destination is
        // the descent prefix + fieldPath + index + indexPath: two writes
        // that target the same exact slot collide; writes to different
        // collection indexes (e.g. InitialAttributes[0] vs
        // InitialAttributes[6]), different descent indexes
        // (Conditions[0] vs Conditions[1]) and different N-dim cell
        // coordinates do not. Append ops never dedup, so two appends on
        // the same collection apply as two additions in order.
        if (op.Op == CompiledTemplateOp.Set)
        {
            for (var i = 0; i < operationsForTemplate.Count; i++)
            {
                var existing = operationsForTemplate[i];
                if (SetOpsCollide(existing, effectivePath, op.Index, op.Descent, op.IndexPath))
                {
                    log.Warning(
                        $"Override template patch '{templateType}:{templateId}.{effectivePath}': "
                        + $"later-loaded mod '{mod.Name}' replaces '{existing.OwnerLabel}'.");
                    operationsForTemplate.RemoveAt(i);
                    PatchCount--;
                    break;
                }
            }
        }

        operationsForTemplate.Add(new LoadedPatchOperation(op.Op, effectivePath, op.Index, op.IndexPath, op.Descent, op.Value, mod.Name));
        PatchCount++;
    }

    /// <summary>
    /// True when two Set ops target the exact same slot and the later
    /// should override the earlier. The dedup key is (fieldPath, index,
    /// descent prefix, multi-dim cell). Indexed collection writes (e.g.
    /// <c>set "InitialAttributes" index=0</c> vs <c>index=6</c>) target
    /// different slots and must not collide; descent and cell coordinates
    /// behave the same way. Append/Insert/Remove never dedup — only Set.
    /// Internal so the test project can lock the dedup invariants directly.
    /// </summary>
    internal static bool SetOpsCollide(
        LoadedPatchOperation existing,
        string newFieldPath,
        int? newIndex,
        IReadOnlyList<TemplateDescentStep> newDescent,
        IReadOnlyList<int> newIndexPath)
    {
        return existing.Op == CompiledTemplateOp.Set
            && string.Equals(existing.FieldPath, newFieldPath, StringComparison.Ordinal)
            && existing.Index == newIndex
            && DescentEquals(existing.Descent, newDescent)
            && IndexPathEquals(existing.IndexPath, newIndexPath);
    }

    // Two descent prefixes match when each step's field, index, and
    // subtype align. Null and empty are treated identically (an op with
    // no outer descent has Descent=null on the wire). Internal so the
    // test project can lock the dedup invariants directly.
    internal static bool DescentEquals(IReadOnlyList<TemplateDescentStep> a, IReadOnlyList<TemplateDescentStep> b)
    {
        var ac = a?.Count ?? 0;
        var bc = b?.Count ?? 0;
        if (ac != bc) return false;
        for (var i = 0; i < ac; i++)
        {
            var sa = a[i];
            var sb = b[i];
            if (!string.Equals(sa.Field, sb.Field, StringComparison.Ordinal)) return false;
            if (sa.Index != sb.Index) return false;
        }
        return true;
    }

    // Cell coordinate equality for matrix Set ops. Empty and null
    // collapse to "no cell coords"; otherwise both length and per-axis
    // values must match. Internal for direct test access.
    internal static bool IndexPathEquals(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        var ac = a?.Count ?? 0;
        var bc = b?.Count ?? 0;
        if (ac != bc) return false;
        for (var i = 0; i < ac; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}

internal sealed class LoadedPatchOperation
{
    public LoadedPatchOperation(
        CompiledTemplateOp op,
        string fieldPath,
        int? index,
        IReadOnlyList<int> indexPath,
        IReadOnlyList<TemplateDescentStep> descent,
        CompiledTemplateValue value,
        string ownerLabel)
    {
        Op = op;
        FieldPath = fieldPath;
        Index = index;
        IndexPath = indexPath;
        Descent = descent;
        Value = value;
        OwnerLabel = ownerLabel;
    }

    public CompiledTemplateOp Op { get; }
    /// <summary>Inner-relative member path on the destination instance.</summary>
    public string FieldPath { get; }
    public int? Index { get; }
    /// <summary>
    /// Multi-dim cell address for Set ops against an N-dimensional array.
    /// Null for non-multi-dim writes; empty list is treated the same as
    /// null. Mutually exclusive with <see cref="Index"/>.
    /// </summary>
    public IReadOnlyList<int> IndexPath { get; }
    /// <summary>
    /// Outer descent prefix as a structural step list. The applier walks
    /// each step in order — descending into element <c>Index</c> of
    /// <c>Field</c>, switching the wrapper's runtime type to <c>Subtype</c>
    /// when the destination is polymorphic — before applying the inner
    /// <see cref="FieldPath"/> write. Null/empty when the patch writes a
    /// top-level member directly.
    /// </summary>
    public IReadOnlyList<TemplateDescentStep> Descent { get; }
    public CompiledTemplateValue Value { get; }
    public string OwnerLabel { get; }
}
