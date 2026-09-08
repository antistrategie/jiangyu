namespace Jiangyu.Shared.Templates;

/// <summary>
/// The template references a compiled value or operation list carries: a value of kind
/// <see cref="CompiledTemplateValueKind.TemplateReference"/> anywhere in it, composites and
/// type constructions recursed. A patch block applies only when every template it refers to
/// exists, so the loader reads this before writing anything.
/// </summary>
public static class TemplateReferences
{
    public static IEnumerable<CompiledTemplateReference> Of(IEnumerable<CompiledTemplateSetOperation>? operations)
    {
        if (operations == null)
            yield break;
        foreach (var op in operations)
        {
            foreach (var reference in Of(op?.Value))
                yield return reference;
        }
    }

    public static IEnumerable<CompiledTemplateReference> Of(CompiledTemplateValue? value)
    {
        if (value == null)
            yield break;

        if (value.Kind == CompiledTemplateValueKind.TemplateReference && value.Reference != null)
            yield return value.Reference;

        foreach (var reference in Of(value.Composite?.Operations))
            yield return reference;
        foreach (var reference in Of(value.TypeConstruction?.Operations))
            yield return reference;
    }
}
