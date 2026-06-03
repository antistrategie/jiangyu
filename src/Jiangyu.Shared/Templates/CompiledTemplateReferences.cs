using System.Collections.Generic;

namespace Jiangyu.Shared.Templates;

/// <summary>
/// Walks a compiled template patch's value tree and yields every outbound reference it
/// carries: a qualified <c>type="modId:Name"</c> construction and an <c>asset=</c>
/// reference, each tagged with its kind and the field path it sits at. One walker so the
/// compile-time cross-check and the xref tooling never disagree about which references a
/// patch holds when the value shape gains a new nesting site.
/// </summary>
public static class CompiledTemplateReferences
{
    public static IEnumerable<(string Kind, string Value, string FieldPath)> Enumerate(CompiledTemplatePatch patch)
    {
        foreach (var op in patch.Set)
            foreach (var reference in FromValue(op.FieldPath, op.Value))
                yield return reference;
    }

    private static IEnumerable<(string Kind, string Value, string FieldPath)> FromValue(
        string fieldPath, CompiledTemplateValue? value)
    {
        if (value is null)
            yield break;

        if (value.Asset is { } asset && !string.IsNullOrEmpty(asset.Name))
            yield return ("asset", asset.Name, fieldPath);

        foreach (var composite in new[] { value.Composite, value.TypeConstruction })
        {
            if (composite is null)
                continue;

            if (!string.IsNullOrEmpty(composite.TypeName) && composite.TypeName.Contains(":"))
                yield return ("type", composite.TypeName, fieldPath);

            if (composite.Operations is null)
                continue;
            foreach (var inner in composite.Operations)
                foreach (var reference in FromValue(inner.FieldPath, inner.Value))
                    yield return reference;
        }
    }
}
