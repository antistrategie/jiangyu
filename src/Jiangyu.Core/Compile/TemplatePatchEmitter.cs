using Jiangyu.Core.Abstractions;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Compile;

/// <summary>
/// Compile-time emitter for the template payload blocks of
/// <c>compiled/jiangyu.json</c>. Validates path syntax and value completeness
/// via <see cref="TemplatePatchPathValidator"/> and validates clone
/// directives for required fields and batch-internal uniqueness. Errors
/// escalate to compile failures so modders fix malformed inputs at compile
/// time rather than discovering silent drops at load time.
/// </summary>
public static class TemplatePatchEmitter
{
    public readonly record struct EmitResult(
        List<CompiledTemplatePatch>? Patches,
        int ErrorCount)
    {
        public bool Success => ErrorCount == 0;
    }

    public readonly record struct CloneEmitResult(
        List<CompiledTemplateClone>? Clones,
        int ErrorCount)
    {
        public bool Success => ErrorCount == 0;
    }

    public static EmitResult Emit(List<CompiledTemplatePatch>? patches, ILogSink log)
    {
        if (patches is null || patches.Count == 0)
            return new EmitResult(patches, 0);

        var emitted = new List<CompiledTemplatePatch>(patches.Count);
        var errorCount = 0;

        foreach (var patch in patches)
        {
            if (patch is null)
            {
                log.Error("Template patch is null.");
                errorCount++;
                continue;
            }

            var templateType = string.IsNullOrWhiteSpace(patch.TemplateType)
                ? null
                : patch.TemplateType!.Trim();
            var templateLabel = templateType ?? "<unspecified>";

            if (string.IsNullOrWhiteSpace(patch.TemplateId))
            {
                log.Error($"Template patch '{templateLabel}': templateId is empty.");
                errorCount++;
                continue;
            }

            if (patch.Set is null || patch.Set.Count == 0)
            {
                log.Error(
                    $"Template patch '{templateLabel}:{patch.TemplateId}' has no 'set' operations.");
                errorCount++;
                continue;
            }

            var emittedOps = new List<CompiledTemplateSetOperation>(patch.Set.Count);
            foreach (var op in patch.Set)
            {
                if (!TryEmitOperation(templateLabel, patch.TemplateId, op, log, out var emittedOp))
                {
                    errorCount++;
                    continue;
                }

                emittedOps.Add(emittedOp);
            }

            if (emittedOps.Count == 0)
                continue;

            emitted.Add(new CompiledTemplatePatch
            {
                TemplateType = patch.TemplateType,
                TemplateId = patch.TemplateId,
                Set = emittedOps,
            });
        }

        return new EmitResult(emitted, errorCount);
    }

    public static CloneEmitResult EmitClones(List<CompiledTemplateClone>? clones, ILogSink log)
    {
        if (clones is null || clones.Count == 0)
            return new CloneEmitResult(clones, 0);

        var emitted = new List<CompiledTemplateClone>(clones.Count);
        var errorCount = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var clone in clones)
        {
            if (clone is null)
            {
                log.Error("Template clone directive is null.");
                errorCount++;
                continue;
            }

            var templateType = clone.TemplateType?.Trim();
            if (string.IsNullOrWhiteSpace(templateType))
            {
                log.Error(
                    $"Template clone '{clone.SourceId} -> {clone.CloneId}': templateType is required.");
                errorCount++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(clone.SourceId))
            {
                log.Error($"Template clone '{templateType}': sourceId is empty.");
                errorCount++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(clone.CloneId))
            {
                log.Error(
                    $"Template clone '{templateType}:{clone.SourceId}': cloneId is empty.");
                errorCount++;
                continue;
            }

            if (string.Equals(clone.SourceId, clone.CloneId, StringComparison.Ordinal))
            {
                log.Error(
                    $"Template clone '{templateType}:{clone.SourceId}': cloneId must differ from sourceId.");
                errorCount++;
                continue;
            }

            var key = templateType + "\0" + clone.CloneId;
            if (!seen.Add(key))
            {
                log.Error(
                    $"Template clone '{templateType}:{clone.CloneId}': duplicate cloneId within this mod.");
                errorCount++;
                continue;
            }

            emitted.Add(new CompiledTemplateClone
            {
                TemplateType = templateType,
                SourceId = clone.SourceId,
                CloneId = clone.CloneId,
            });
        }

        return new CloneEmitResult(emitted, errorCount);
    }

    private static bool TryEmitOperation(
        string templateLabel,
        string templateId,
        CompiledTemplateSetOperation? op,
        ILogSink log,
        out CompiledTemplateSetOperation emitted)
    {
        emitted = default!;

        if (op is null)
        {
            log.Error($"Template patch '{templateLabel}:{templateId}' has a null set operation.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(op.FieldPath))
        {
            log.Error($"Template patch '{templateLabel}:{templateId}' has an empty fieldPath.");
            return false;
        }

        var effectivePath = op.FieldPath;

        if (!TemplatePatchPathValidator.IsSupportedFieldPath(effectivePath))
        {
            log.Error(
                $"Template patch '{templateLabel}:{templateId}.{effectivePath}' has unsupported path syntax. "
                + "Supported: dotted names (a.b.c) and indexers (name[N]). Parentheses are rejected.");
            return false;
        }

        if (!TemplatePatchPathValidator.TryValidateOpShape(op, effectivePath, out var opShapeError))
        {
            log.Error($"Template patch '{templateLabel}:{templateId}.{effectivePath}' — {opShapeError}");
            return false;
        }

        emitted = new CompiledTemplateSetOperation
        {
            Op = op.Op,
            FieldPath = effectivePath,
            Index = op.Index,
            Value = op.Value,
        };
        return true;
    }

}
