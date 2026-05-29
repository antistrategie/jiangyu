using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Templates;

/// <summary>
/// Pre-validation pass collecting small ergonomic auto-fills that close
/// common omissions in modder-authored KDL. Each filler targets a
/// recurring pattern where the modder would otherwise duplicate info
/// already implicit elsewhere in the file.
///
/// Runs as a single tree walk over the parsed patches, with one helper
/// per filler. Each helper is idempotent: re-running over an
/// already-filled tree is a no-op.
///
/// Companion to <see cref="NodeGuidAutoFiller"/>, which handles the
/// ConversationNode/Container Guid concern. Kept separate because the
/// fillers here key off either the patch's <c>(TemplateType, TemplateId)</c>
/// or the composite's <c>TypeName</c>, while NodeGuid keys off the CLR
/// hierarchy via the catalog.
/// </summary>
internal static class CompositeAutoFillers
{
    /// <summary>
    /// Pre-validation auto-fills that depend on patch-level metadata
    /// (TemplateType, TemplateId) but not on the validator's TypeName
    /// inference. Currently: clone-identity injection.
    /// </summary>
    public static void ApplyPreValidation(IEnumerable<CompiledTemplatePatch>? patches)
    {
        if (patches is null) return;
        foreach (var patch in patches)
            FillCloneIdentity(patch);
    }

    /// <summary>
    /// Post-validation auto-fills that key off resolved composite
    /// <see cref="CompiledTemplateComposite.TypeName"/>. Inferred
    /// composites (TypeName="" at parse time) get filled by the catalog
    /// validator, so these fillers run afterwards to see the concrete
    /// type.
    /// </summary>
    public static void ApplyPostValidation(IEnumerable<CompiledTemplatePatch>? patches)
    {
        if (patches is null) return;
        foreach (var patch in patches)
        {
            foreach (var op in patch.Set)
                Walk(op);
        }
    }

    /// <summary>
    /// Inject the identity field that always equals the cloneId:
    /// <c>SoundBank.bankId</c> (string-FNV'd at apply) or
    /// <c>ConversationTemplate.Path</c>. Skipped if the modder set it
    /// explicitly.
    /// </summary>
    private static void FillCloneIdentity(CompiledTemplatePatch patch)
    {
        if (string.IsNullOrEmpty(patch.TemplateId)) return;

        string? identityField = patch.TemplateType switch
        {
            "SoundBank" => "bankId",
            "ConversationTemplate" => "Path",
            _ => null,
        };
        if (identityField is null) return;

        foreach (var op in patch.Set)
        {
            if (op.Op == CompiledTemplateOp.Set
                && string.Equals(op.FieldPath, identityField, StringComparison.Ordinal))
                return;
        }

        patch.Set.Insert(0, new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Set,
            FieldPath = identityField,
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.String,
                String = patch.TemplateId,
            },
        });
    }

    private static void Walk(CompiledTemplateSetOperation op)
    {
        if (op.Value is not { } value) return;
        if (value.Kind == CompiledTemplateValueKind.Composite && value.Composite is { } composite)
        {
            FillSoundId(composite);
            FillVariationCopyCount(composite);
            foreach (var inner in composite.Operations)
                Walk(inner);
        }
        else if (value.Kind == CompiledTemplateValueKind.TypeConstruction
            && value.TypeConstruction is { } handler)
        {
            foreach (var inner in handler.Operations)
                Walk(inner);
        }
    }

    /// <summary>
    /// For <c>Stem.Sound</c> composites, default <c>id</c> to the
    /// modder-set <c>name</c> string when <c>id</c> is omitted. Both
    /// fields FNV-1a-hash to ints at apply time; within a SoundBank,
    /// uniqueness only requires <c>name</c> to be distinct (which the
    /// modder already needs for human-readable authoring).
    /// </summary>
    private static void FillSoundId(CompiledTemplateComposite composite)
    {
        if (!IsSoundType(composite.TypeName)) return;

        CompiledTemplateSetOperation? nameOp = null;
        var hasId = false;
        foreach (var op in composite.Operations)
        {
            if (op.Op != CompiledTemplateOp.Set) continue;
            if (string.Equals(op.FieldPath, "name", StringComparison.Ordinal))
                nameOp = op;
            else if (string.Equals(op.FieldPath, "id", StringComparison.Ordinal))
                hasId = true;
        }
        if (hasId || nameOp?.Value is not { Kind: CompiledTemplateValueKind.String, String: { } nameStr })
            return;

        // Sound.id is a HashableIdFieldRegistry-registered Int32 — the
        // validator FNV-1a-hashes a string-authored value into the int
        // at compile time. This pass runs post-validator (needs the
        // resolved TypeName), so we hash directly and write the Int32.
        composite.Operations.Add(new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Set,
            FieldPath = "id",
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Int32,
                Int32 = HashableIdFieldRegistry.Fnv1a32(nameStr),
            },
        });
    }

    /// <summary>
    /// For <c>VariationConversationNode</c> composites, sync the parallel
    /// <c>VariationCopyCount</c> array with the count of
    /// <c>append "Variations"</c> ops. Each variation needs a copy-count
    /// entry; vanilla data ships with <c>1</c> per variation. Missing
    /// entries make the playback path silently skip that branch.
    /// </summary>
    private static void FillVariationCopyCount(CompiledTemplateComposite composite)
    {
        if (!string.Equals(composite.TypeName, "VARIATION", StringComparison.Ordinal)
            && !composite.TypeName.EndsWith("VariationConversationNode", StringComparison.Ordinal))
            return;

        var variationAppends = 0;
        var copyCountAppends = 0;
        var copyCountIsCleared = false;
        foreach (var op in composite.Operations)
        {
            if (string.Equals(op.FieldPath, "Variations", StringComparison.Ordinal)
                && op.Op == CompiledTemplateOp.Append)
                variationAppends++;
            else if (string.Equals(op.FieldPath, "VariationCopyCount", StringComparison.Ordinal))
            {
                if (op.Op == CompiledTemplateOp.Append) copyCountAppends++;
                else if (op.Op == CompiledTemplateOp.Clear) copyCountIsCleared = true;
            }
        }

        // Modder cleared the parallel array and is rebuilding from
        // scratch, or set it explicitly with index= ops, or appended
        // enough entries already. Don't fight them.
        if (copyCountIsCleared) return;
        if (copyCountAppends >= variationAppends) return;

        for (var i = copyCountAppends; i < variationAppends; i++)
        {
            composite.Operations.Add(new CompiledTemplateSetOperation
            {
                Op = CompiledTemplateOp.Append,
                FieldPath = "VariationCopyCount",
                Value = new CompiledTemplateValue
                {
                    Kind = CompiledTemplateValueKind.Int32,
                    Int32 = 1,
                },
            });
        }
    }

    private static bool IsSoundType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        // Match both the modder-facing short name ("Sound") and the FQN
        // forms that show up after validator resolution.
        return string.Equals(typeName, "Sound", StringComparison.Ordinal)
            || string.Equals(typeName, "Stem.Sound", StringComparison.Ordinal)
            || string.Equals(typeName, "Il2CppStem.Sound", StringComparison.Ordinal);
    }
}
