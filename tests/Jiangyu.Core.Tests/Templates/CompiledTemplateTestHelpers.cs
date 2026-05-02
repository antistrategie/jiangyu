using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Tests.Templates;

/// <summary>
/// Tiny construction helpers that keep test fixtures readable now that
/// composite/handler bodies are directive lists rather than name→value
/// dictionaries. Each test still spells out one operation per field; these
/// just hide the ceremony of <c>new() { Op = Set, FieldPath = ..., Value = ...
/// }</c> at every site.
/// </summary>
internal static class CompiledTemplateTestHelpers
{
    public static CompiledTemplateSetOperation SetOp(string fieldPath, CompiledTemplateValue value) =>
        new() { Op = CompiledTemplateOp.Set, FieldPath = fieldPath, Value = value };

    public static List<CompiledTemplateSetOperation> SetOps(
        params (string fieldPath, CompiledTemplateValue value)[] entries)
    {
        var list = new List<CompiledTemplateSetOperation>(entries.Length);
        foreach (var (fieldPath, value) in entries)
        {
            list.Add(SetOp(fieldPath, value));
        }
        return list;
    }

    public static CompiledTemplateValue ValueAt(this CompiledTemplateComposite composite, string fieldPath)
    {
        foreach (var op in composite.Operations)
        {
            if (op.Op == CompiledTemplateOp.Set
                && string.Equals(op.FieldPath, fieldPath, StringComparison.Ordinal))
            {
                return op.Value!;
            }
        }
        throw new KeyNotFoundException(
            $"composite has no Set operation on '{fieldPath}'. Operations present: "
            + string.Join(", ", composite.Operations.Select(o => $"{o.Op} {o.FieldPath}")));
    }
}
