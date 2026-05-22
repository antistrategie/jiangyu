using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Models;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Templates;

/// <summary>
/// Pre-validation pass that resolves modder-authored
/// <c>set "RoleGuid" "Entity"</c> string values inside
/// <c>ConversationTemplate</c> patches/clones to the int <c>Guid</c> of
/// the matching <c>Role</c>.
///
/// The role-name → Guid map comes from the asset index's per-asset
/// <see cref="AssetEntry.Roles"/> field. Clones inherit their source's
/// roles; patches look up the target template's roles. Modder-defined
/// roles in the same patch/clone are layered on top (their <c>Guid</c>
/// must be specified explicitly today; symbolic resolution into
/// modder-defined roles is a follow-up).
///
/// Resolution mutates the compiled tree in place: the
/// <see cref="CompiledTemplateValue"/> for a matched RoleGuid op goes
/// from <c>Kind=String</c> to <c>Kind=Int32</c>, after which the catalog
/// validator sees a normal numeric assignment.
///
/// Mirrors the <see cref="NodeGuidAutoFiller"/> shape: a focused
/// pre-validation walker scoped to one specific compile-time concern.
/// </summary>
internal static class RoleGuidResolver
{
    /// <summary>Returns the error count.</summary>
    public static int Apply(
        IEnumerable<CompiledTemplatePatch>? patches,
        IEnumerable<CompiledTemplateClone>? clones,
        IReadOnlyList<AssetEntry>? indexedAssets,
        ILogSink log)
    {
        if (indexedAssets is null || indexedAssets.Count == 0) return 0;

        // Modder-side: cloneId → sourceId. A patch on a cloned conversation
        // carries the cloneId as its TemplateId, but the Roles come from
        // the source. The map lets us follow the chain at resolve time.
        var cloneSources = new Dictionary<string, string>(StringComparer.Ordinal);
        if (clones != null)
        {
            foreach (var clone in clones)
            {
                if (!string.Equals(clone.TemplateType, "ConversationTemplate", StringComparison.Ordinal))
                    continue;
                if (!string.IsNullOrEmpty(clone.CloneId) && !string.IsNullOrEmpty(clone.SourceId))
                    cloneSources[clone.CloneId] = clone.SourceId;
            }
        }

        var errors = 0;
        if (patches != null)
        {
            foreach (var patch in patches)
            {
                if (!string.Equals(patch.TemplateType, "ConversationTemplate", StringComparison.Ordinal))
                    continue;

                // Follow the clone chain to find the asset that holds the
                // role definitions. A patch on a vanilla conversation
                // (templateId is a vanilla Path) looks up directly; a
                // patch on a cloned conversation hops to the source first.
                var lookupKey = cloneSources.TryGetValue(patch.TemplateId, out var sourceId)
                    ? sourceId
                    : patch.TemplateId;

                var roles = ConversationRoleLookup.FindRoles(lookupKey, indexedAssets);
                if (roles is null) continue;

                foreach (var op in patch.Set)
                    errors += WalkOperation(op, roles, patch.TemplateId, log);
            }
        }

        return errors;
    }

    private static int WalkOperation(
        CompiledTemplateSetOperation op,
        IReadOnlyList<AssetEntryRole>? roles,
        string templateContext,
        ILogSink log)
    {
        var errors = 0;
        errors += MaybeResolveValue(op, roles, templateContext, log);
        if (op.Value is { Kind: CompiledTemplateValueKind.Composite, Composite: { } composite })
        {
            foreach (var inner in composite.Operations)
                errors += WalkOperation(inner, roles, templateContext, log);
        }
        else if (op.Value is { Kind: CompiledTemplateValueKind.HandlerConstruction, HandlerConstruction: { } handler })
        {
            foreach (var inner in handler.Operations)
                errors += WalkOperation(inner, roles, templateContext, log);
        }
        return errors;
    }

    private static int MaybeResolveValue(
        CompiledTemplateSetOperation op,
        IReadOnlyList<AssetEntryRole>? roles,
        string templateContext,
        ILogSink log)
    {
        if (op.Value is not { Kind: CompiledTemplateValueKind.String, String: { } literal })
            return 0;
        if (!IsRoleGuidField(op.FieldPath)) return 0;

        if (roles is null || roles.Count == 0)
        {
            log.Error(
                $"ConversationTemplate '{templateContext}': "
                + $"cannot resolve RoleGuid \"{literal}\" — source conversation has no indexed roles. "
                + "Rebuild the asset index ('jiangyu assets index') or specify the int Guid explicitly.");
            return 1;
        }

        foreach (var role in roles)
        {
            if (!string.Equals(role.Name, literal, StringComparison.Ordinal)) continue;
            op.Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Int32,
                Int32 = role.Guid,
            };
            return 0;
        }

        var known = string.Join(", ", roles.Select(r => r.Name));
        log.Error(
            $"ConversationTemplate '{templateContext}': "
            + $"RoleGuid \"{literal}\" does not match any role. Known: {known}.");
        return 1;
    }

    /// <summary>
    /// True when the field path's terminal segment is <c>RoleGuid</c>.
    /// Covers SAY/CHOICE node compositions where the modder wrote a role
    /// name string against the int <c>RoleGuid</c> field.
    /// </summary>
    private static bool IsRoleGuidField(string fieldPath)
    {
        if (string.IsNullOrEmpty(fieldPath)) return false;
        var lastDot = fieldPath.LastIndexOf('.');
        var terminal = lastDot >= 0 ? fieldPath[(lastDot + 1)..] : fieldPath;
        var bracket = terminal.IndexOf('[');
        if (bracket >= 0) terminal = terminal[..bracket];
        return string.Equals(terminal, "RoleGuid", StringComparison.Ordinal);
    }
}
