using Jiangyu.Shared.Templates;

namespace Jiangyu.Loader.Templates;

// Read-only descent navigation, exposed for callers that need the live object a descent names rather
// than a write applied to it (the localisation injector walks to a BaseLocalizedString this way). Reuses
// the same member-read and collection-index primitives the operation walker uses, so it sees the same
// fields and indexing rules.
internal sealed partial class TemplatePatchApplier
{
    internal static bool TryNavigateDescent(
        object root, IReadOnlyList<TemplateDescentStep> descent, out object target, out string error)
    {
        target = root;
        error = null;
        if (descent == null)
            return true;

        foreach (var step in descent)
        {
            if (!TryReadMember(target, step.Field, out var value, out _, out var readError))
            {
                error = $"descent step '{step.Field}': {readError}";
                target = null;
                return false;
            }
            if (value == null)
            {
                error = $"descent step '{step.Field}' is null.";
                target = null;
                return false;
            }
            if (step.Index is { } index)
            {
                if (!TryIndexInto(value, index, out var element, out _, out var indexError))
                {
                    error = $"descent step '{step.Field}' index {index}: {indexError}";
                    target = null;
                    return false;
                }
                value = element;
            }
            target = value;
        }

        return true;
    }
}
