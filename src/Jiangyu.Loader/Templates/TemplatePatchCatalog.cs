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
    // Middle key: templateId. Inner list: operations in load order. A later set on
    // the same slot wins when the ops apply; append ops always add a new entry so N
    // appends on the same field apply N new elements in authored/load order.
    private readonly Dictionary<string, Dictionary<string, List<LoadedPatchOperation>>> _patches
        = new(StringComparer.Ordinal);

    public int PatchCount { get; private set; }

    // The position counter: each op takes the next value, so a template's ops gathered
    // under several type names order by load order across every mod.
    private int _nextSequence;

    // Type names that address the same templates as a given name, cached: the catalogue is
    // fixed after load.
    private readonly Dictionary<string, List<string>> _aliasNames = new(StringComparer.Ordinal);
    private readonly Func<string, string, bool> _sameTemplateSpace;

    // The entry name each spelling files under: one entry per type, named canonically, so a
    // qualified and a short name of one type share an entry, and the clone catalogue's entry
    // for the type has the same name.
    private readonly Dictionary<string, string> _entryNames = new(StringComparer.Ordinal);
    private readonly Func<string, string> _canonicalName;

    public TemplatePatchCatalog(Func<string, string, bool> sameTemplateSpace = null, Func<string, string> canonicalName = null)
    {
        _sameTemplateSpace = sameTemplateSpace ?? TemplateRuntimeAccess.SameTemplateSpace;
        _canonicalName = canonicalName ?? TemplateRuntimeAccess.CanonicalTypeName;
    }

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
            && _patches.TryGetValue(EntryNameFor(templateType), out var byId)
            && byId.TryGetValue(templateId, out ops);
    }

    /// <summary>The entry names that address the same templates as
    /// <paramref name="templateType"/>: its own entry name, and every entry whose type derives
    /// from or is an ancestor of its type (a DataTemplate is registered in every ancestor map).
    /// Ops under any of them target the same object.</summary>
    public IReadOnlyList<string> AliasNames(string templateType)
    {
        if (templateType == null)
            return Array.Empty<string>();
        var entryName = EntryNameFor(templateType);
        if (_aliasNames.TryGetValue(entryName, out var names))
            return names;
        names = new List<string> { entryName };
        foreach (var typeName in _patches.Keys)
        {
            if (!names.Contains(typeName) && _sameTemplateSpace(entryName, typeName))
                names.Add(typeName);
        }

        _aliasNames[entryName] = names;
        return names;
    }

    /// <summary>A template's ops under every alias of <paramref name="templateType"/>, in
    /// load order across them, so a later mod still wins whichever name it used.</summary>
    public List<LoadedPatchOperation> OperationsAcrossAliases(string templateType, string templateId)
    {
        var all = new List<LoadedPatchOperation>();
        foreach (var typeName in AliasNames(templateType))
        {
            if (TryGetOperations(typeName, templateId, out var ops))
                all.AddRange(ops);
        }

        if (all.Count > 1)
            all.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        return all;
    }

    /// <summary>
    /// The set of top-level member names the patch ops for
    /// <paramref name="templateId"/> write to, under every alias of the type. The clone applier uses this to
    /// tell which non-collection fields a clone authored itself, so
    /// re-inheritance from a cloned source fills only the ones the clone left
    /// untouched (never overwriting an authored value, nor sharing a mutable
    /// sub-object the clone will patch). A nested op (descent into a field, or a
    /// dotted/indexed path) counts against its outermost member.
    /// </summary>
    public HashSet<string> TouchedTopLevelFields(string templateType, string templateId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in OperationsAcrossAliases(templateType, templateId))
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

        log.Debug(
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

        templateType = EntryNameFor(templateType);
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

        // A later set on the same slot wins: it applies after the earlier one, and an
        // earlier one that lands later (its block was held) is skipped by the applier.
        // Both stay in the catalogue, so a held later block never takes a ready earlier
        // mod's value with it. The slot is the descent prefix + fieldPath + index +
        // indexPath: writes to different collection indexes, descent indexes or N-dim
        // cells do not collide, and append ops never do.
        if (op.Op == CompiledTemplateOp.Set)
        {
            foreach (var existing in operationsForTemplate)
            {
                if (SetOpsCollide(existing, effectivePath, op.Index, op.Descent, op.IndexPath))
                {
                    log.Warning(
                        $"Override template patch '{templateType}:{templateId}.{effectivePath}': "
                        + $"later-loaded mod '{mod.Name}' replaces '{existing.OwnerLabel}'.");
                    break;
                }
            }
        }

        operationsForTemplate.Add(new LoadedPatchOperation(op.Op, effectivePath, op.Index, op.IndexPath, op.Descent, op.Value, mod.Name, _nextSequence++));
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
        string ownerLabel,
        int sequence = 0)
    {
        Sequence = sequence;
        Op = op;
        FieldPath = fieldPath;
        Index = index;
        IndexPath = indexPath;
        Descent = descent;
        Value = value;
        OwnerLabel = ownerLabel;
    }

    public CompiledTemplateOp Op { get; }
    /// <summary>Position in the load order across every mod and template type. Ops on one
    /// template gathered under several type names are ordered by it.</summary>
    public int Sequence { get; }
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
