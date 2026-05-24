using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Jiangyu.Shared.Templates;
using MelonLoader;

namespace Jiangyu.Loader.Templates;

// JIANGYU-CONTRACT: Live template identity for patching is the serialised
// m_ID string. The applier resolves a patch to its target via
// DataTemplateLoader.TryGet<T>(m_ID), which reads m_TemplateMaps and so
// sees both game-native templates and Jiangyu-injected clones. (collection,
// pathId) and native pointers are not used; m_ID is the only identifier
// that's stable across game runs and persistable in source. Subtypes
// without a readable m_ID surface as a clear log warning rather than
// silently dropping patches. Field paths are walked as dotted segments
// with optional [N] indexers; intermediate segments may be reference
// types or value types (the latter are read-boxed-and-written-back after
// the terminal set).

/// <summary>
/// One-shot applier that takes the merged catalogue of compiled template
/// patches and writes each scalar set operation into the matching live
/// template once the game has materialised the DataTemplate cache for that
/// type. Callers are expected to invoke <see cref="TryApply"/> repeatedly;
/// it no-ops until templates are present, then applies once per type and
/// latches for that type.
/// </summary>
internal sealed partial class TemplatePatchApplier
{
    private readonly TemplatePatchCatalog _catalog;
    private readonly ModAssetResolver _assetResolver;
    private readonly HashSet<string> _appliedTypes = new(StringComparer.Ordinal);

    public TemplatePatchApplier(TemplatePatchCatalog catalog, ModAssetResolver assetResolver = null)
    {
        _catalog = catalog;
        _assetResolver = assetResolver;
    }

    public bool HasPendingPatches
    {
        get
        {
            if (!_catalog.HasPatches)
                return false;

            foreach (var typeEntry in _catalog.EnumerateByType())
            {
                if (!_appliedTypes.Contains(typeEntry.Key))
                    return true;
            }

            return false;
        }
    }

    public int TryApply(MelonLogger.Instance log)
    {
        if (!_catalog.HasPatches)
            return 0;

        var totalApplied = 0;

        foreach (var typeEntry in _catalog.EnumerateByType())
        {
            if (_appliedTypes.Contains(typeEntry.Key))
                continue;

            totalApplied += TryApplyType(typeEntry.Key, typeEntry.Value, log);
        }

        return totalApplied;
    }

    private int TryApplyType(
        string templateTypeName,
        Dictionary<string, List<LoadedPatchOperation>> patchesForType,
        MelonLogger.Instance log)
    {
        // Use GetAllTemplates once only to trigger materialisation and detect
        // "templates not ready yet" (return 0, retry next scene) vs terminal
        // type-resolution failure. Per-template lookups below go through
        // DataTemplateLoader.TryGet<T> directly, which reads m_TemplateMaps and
        // therefore sees both game-native templates and Jiangyu clones.
        var liveTemplates = TemplateRuntimeAccess.GetAllTemplates(templateTypeName, out var resolvedType, out var resolveError);

        if (resolvedType == null)
        {
            var expectedOps = patchesForType.Values.Sum(inner => inner.Count);
            log.Warning(
                $"Template patch: cannot resolve type '{templateTypeName}' ({resolveError}); "
                + $"skipping {expectedOps} op(s).");
            _appliedTypes.Add(templateTypeName);
            return 0;
        }

        if (liveTemplates.Count == 0)
            return 0;

        var applied = 0;
        var missingTemplate = 0;
        var missingMember = 0;
        var conversionFailed = 0;

        foreach (var templateEntry in patchesForType)
        {
            if (!TemplateRuntimeAccess.TryGetTemplateById(
                    templateTypeName, templateEntry.Key,
                    out var template, out _, out var lookupError))
            {
                missingTemplate += templateEntry.Value.Count;
                var reason = string.IsNullOrEmpty(lookupError) ? "not found" : lookupError;
                log.Warning(
                    $"Template patch: no live {templateTypeName} with m_ID '{templateEntry.Key}' ({reason}); "
                    + $"skipping {templateEntry.Value.Count} op(s).");
                continue;
            }

            var opsAppliedThisTemplate = 0;
            foreach (var op in templateEntry.Value)
            {
                switch (TryApplyOperation(template, templateTypeName, templateEntry.Key, op, _assetResolver, log))
                {
                    case ApplyOutcome.Applied:
                        applied++;
                        opsAppliedThisTemplate++;
                        break;
                    case ApplyOutcome.MemberMissing:
                        missingMember++;
                        break;
                    case ApplyOutcome.ConversionFailed:
                        conversionFailed++;
                        break;
                }
            }

            // After applying ops to a template, invoke OnAfterDeserialize if
            // the type exposes it. Unity calls OnAfterDeserialize when an
            // asset is loaded; subsequent runtime field writes don't trigger
            // it again. Types like Stem.SoundBank keep a derived runtime
            // cache (SoundBankRuntime: id->Sound dictionary) that's built
            // from the serialised list during OnAfterDeserialize. Patching
            // the list without re-running the rebuild leaves the cache
            // stale, so playback lookups miss our new entries. Generic
            // reflection invocation keeps the hook usable for any type
            // following the ISerializationCallbackReceiver convention.
            if (opsAppliedThisTemplate > 0)
            {
                // SoundBank-specific pre-OnAfterDeserialize alignment.
                // busIndices is a parallel array to sounds; if the modder's
                // patch grew sounds[] without growing busIndices,
                // OnAfterDeserialize would throw IndexOutOfRange. Align
                // busIndices to sounds.Count (extending with 0 = default
                // bus) so the modder never has to do this manually.
                if (templateTypeName == "SoundBank")
                    TrySoundBankAlignBusIndices(template, templateEntry.Key, log);

                TryInvokeOnAfterDeserialize(template, templateTypeName, templateEntry.Key, log);
                // Type-specific post-apply wiring. SoundBank needs each new
                // Sound back-pointed at the bank and registered in the
                // SoundBankRuntime cache that playback consults. The bank's
                // own AddSound(Sound) method handles this, but we already
                // appended to sounds[] manually; calling AddSound here just
                // does the wiring side-effects (m_Bank, m_Bus, runtime
                // register) without re-appending. See SoundBank type dump:
                // AddSound checks IndexOfSound and short-circuits the
                // append when the sound is already in the list.
                if (templateTypeName == "SoundBank")
                    TrySoundBankPostApplyWiring(template, templateEntry.Key, log);
            }
        }

        _appliedTypes.Add(templateTypeName);
        log.Msg(
            $"Applied {applied} {templateTypeName} patch op(s). "
            + $"[skipped: missingTemplate={missingTemplate} missingMember={missingMember} "
            + $"conversion={conversionFailed}]");
        return applied;
    }

    private static string FormatPrefix(string templateTypeName, string templateId, LoadedPatchOperation op)
        => $"Template patch '{templateTypeName}:{templateId}.{op.FieldPath}' (mod '{op.OwnerLabel}'): ";

    // SoundBank-specific pre-OnAfterDeserialize alignment. busIndices is a
    // parallel array to sounds (one int per sound, pointing into buses[]).
    // OnAfterDeserialize iterates both in lockstep and throws
    // IndexOutOfRange if busIndices is shorter than sounds. When a modder
    // appends to sounds without explicitly appending matching busIndices
    // entries, align the lengths by extending busIndices with 0 (default
    // bus). Synthesises an internal Append op per missing entry so all
    // the existing array-resize machinery handles the heavy lifting.
    private void TrySoundBankAlignBusIndices(
        Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase bank,
        string bankId,
        MelonLogger.Instance log)
    {
        if (bank == null) return;

        if (!TryReadMember(bank, "sounds", out var soundsObj, out var soundsType, out _) || soundsObj == null)
            return;
        if (!TryReadMember(bank, "busIndices", out var busObj, out _, out _) || busObj == null)
            return;

        var soundsCount = (int)(soundsType.GetProperty("Count")?.GetValue(soundsObj) ?? 0);
        int busCount;
        try
        {
            var busLengthProp = busObj.GetType().GetProperty("Length") ?? busObj.GetType().GetProperty("Count");
            busCount = (int)(busLengthProp?.GetValue(busObj) ?? 0);
        }
        catch
        {
            return;
        }

        if (busCount >= soundsCount) return;

        var missing = soundsCount - busCount;
        var appendOp = new LoadedPatchOperation(
            CompiledTemplateOp.Append,
            "busIndices",
            index: null,
            indexPath: Array.Empty<int>(),
            descent: Array.Empty<Jiangyu.Shared.Templates.TemplateDescentStep>(),
            value: new Jiangyu.Shared.Templates.CompiledTemplateValue
            {
                Kind = Jiangyu.Shared.Templates.CompiledTemplateValueKind.Int32,
                Int32 = 0,
            },
            ownerLabel: "soundbank:align-busIndices");

        var applied = 0;
        for (var i = 0; i < missing; i++)
        {
            if (TryApplyOperation(bank, "SoundBank", bankId, appendOp, _assetResolver, log) == ApplyOutcome.Applied)
                applied++;
            else
                break;
        }

        if (applied > 0)
            log.Msg($"Template patch 'SoundBank:{bankId}': auto-extended busIndices by {applied} (sounds.Count={soundsCount}, prior busIndices.Length={busCount}).");
    }

    // SoundBank-specific post-apply fixup. Iterates bank.sounds[]; for any
    // Sound whose m_Bank back-pointer is null (the shape modder-appended
    // entries land in), invokes SoundBank.AddSound(sound) so the bank
    // performs the same wiring vanilla Sounds receive: set m_Bank, set
    // m_Bus from the default bus, and register the Sound with the
    // SoundBankRuntime cache that playback consults at lookup time.
    private static void TrySoundBankPostApplyWiring(
        Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase bank,
        string bankId,
        MelonLogger.Instance log)
    {
        if (bank == null) return;

        if (!TryReadMember(bank, "sounds", out var soundsObj, out var soundsType, out var readError) || soundsObj == null)
        {
            log.Warning($"Template patch 'SoundBank:{bankId}': cannot read sounds for post-apply wiring ({readError}).");
            return;
        }

        var countProp = soundsType.GetProperty("Count");
        var getItem = soundsType.GetMethod("get_Item", new[] { typeof(int) });
        if (countProp == null || getItem == null)
        {
            log.Warning($"Template patch 'SoundBank:{bankId}': sounds collection {soundsType.FullName} has no Count/get_Item; skipping post-apply wiring.");
            return;
        }

        var bankType = bank.GetType();
        var addSound = bankType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "AddSound"
                                 && m.GetParameters().Length == 1
                                 && m.GetParameters()[0].ParameterType.Name == "Sound");
        if (addSound == null)
        {
            log.Warning($"Template patch 'SoundBank:{bankId}': no AddSound(Sound) method; skipping post-apply wiring.");
            return;
        }

        var count = (int)countProp.GetValue(soundsObj);
        // Il2CppInterop wrappers expose typed members as PROPERTIES, not
        // .NET fields. The "m_Bank" field shown by the catalogue is a
        // property on the wrapper that delegates to the native field.
        // GetField("m_Bank") returns the wrapper's NativeFieldInfoPtr
        // IntPtr at best, not the managed SoundBank value we want.
        System.Reflection.PropertyInfo bankProp = null;
        System.Reflection.PropertyInfo idProp = null;
        for (var i = 0; i < count; i++)
        {
            object sound;
            try { sound = getItem.Invoke(soundsObj, new object[] { i }); }
            catch { continue; }
            if (sound == null) continue;

            var soundType = sound.GetType();
            bankProp ??= soundType.GetProperty("m_Bank") ?? soundType.GetProperty("Bank");
            idProp ??= soundType.GetProperty("id") ?? soundType.GetProperty("ID");
            if (bankProp == null) continue;

            object currentBank;
            try { currentBank = bankProp.GetValue(sound); }
            catch { continue; }
            if (currentBank != null) continue;

            int soundId = -1;
            try { soundId = idProp != null ? (int)idProp.GetValue(sound) : -1; }
            catch { }

            try
            {
                addSound.Invoke(bank, new[] { sound });
                log.Msg($"Template patch 'SoundBank:{bankId}': wired Sound id={soundId} (index {i}) via AddSound.");
            }
            catch (Exception ex)
            {
                var root = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                log.Warning($"Template patch 'SoundBank:{bankId}': AddSound at index {i} (sound id={soundId}) threw {root.GetType().Name}: {root.Message}.");
            }
        }
    }

    private static void TryInvokeOnAfterDeserialize(
        Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase template,
        string templateTypeName,
        string templateId,
        MelonLogger.Instance log)
    {
        if (template == null) return;
        var type = template.GetType();
        var method = type.GetMethod(
            "OnAfterDeserialize",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        if (method == null) return;
        try
        {
            method.Invoke(template, null);
        }
        catch (Exception ex)
        {
            // TargetInvocationException wraps the real exception; surface the
            // inner cause and stack so callers can diagnose null fields,
            // index-out-of-bounds in parallel arrays, etc.
            var root = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null
                ? tie.InnerException
                : ex;
            log.Warning(
                $"Template patch '{templateTypeName}:{templateId}': OnAfterDeserialize threw {root.GetType().Name}: {root.Message}. "
                + "Derived state may be stale; subsequent lookups against patched collections may miss new entries.");
            if (!string.IsNullOrEmpty(root.StackTrace))
                log.Warning($"  at: {root.StackTrace.Split('\n').FirstOrDefault()?.Trim()}");
        }
    }

    // For Il2Cpp wrappers, identity is the native object pointer, not
    // Il2CppObjectBase.Equals (which compares per-wrapper GC handles and so
    // returns false for two wrappers pooled over the same native object).
    // Everything else (scalar boxed values, strings, enums) goes through
    // object.Equals as normal.
    private static bool ReadbackMatches(object written, object readback)
    {
        if (written is Il2CppObjectBase writtenObj && readback is Il2CppObjectBase readbackObj)
            return writtenObj.Pointer == readbackObj.Pointer;
        return Equals(written, readback);
    }

    // Identity formatter for log lines. Each template base class has a
    // different identity field, so dispatch by type rather than probe-then-
    // guess. DataTemplate subtypes expose `m_ID`; non-DataTemplate
    // ScriptableObjects expose `Object.name`; freshly-constructed composite
    // support types (e.g. a new `Perk`) have no identity — log the wrapper
    // type name alone, which is accurate (it's a brand-new object).
    private static string FormatValue(object value)
    {
        if (value is not Il2CppObjectBase il2Cpp)
            return value?.ToString() ?? "null";

        var typeName = value.GetType().Name;
        string id;
        if (typeof(Il2CppMenace.Tools.DataTemplate).IsAssignableFrom(value.GetType()))
            id = TemplateRuntimeAccess.ReadTemplateId(il2Cpp);
        else if (il2Cpp is UnityEngine.Object unityObj)
            id = unityObj.name;
        else
            id = null;

        return string.IsNullOrWhiteSpace(id) ? typeName : $"{typeName} '{id}'";
    }

    // One segment of a parsed dotted field path. Index is non-null when
    // the segment carried a bracketed [N] indexer; the operations partial
    // dispatches on it to either bind the array element or fall through
    // to a plain member set.
    private readonly struct PathSegment
    {
        public PathSegment(string name, int? index)
        {
            Name = name;
            Index = index;
        }

        public string Name { get; }
        public int? Index { get; }
    }

    private enum ApplyOutcome
    {
        Applied,
        MemberMissing,
        ConversionFailed,
    }
}
