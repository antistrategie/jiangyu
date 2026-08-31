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
                if (!TryResolveDescentIndex(value, index, out var resolved, out var resolveError)
                    || !TryIndexInto(value, resolved, out var element, out _, out resolveError))
                {
                    error = $"descent step '{step.Field}' index {FormatIndex(index)}: {resolveError}";
                    target = null;
                    return false;
                }
                value = element;
            }
            target = value;
        }

        return true;
    }

    // A negative descent index counts back from the end (-1 is the last element), which is how the
    // localisation coordinate names an element a patch appended: appends land at the end in op order,
    // so only the distance from the end is known when the POT is minted. Resolving it needs the live
    // length, so a collection exposing neither Length nor Count cannot serve a from-end index.
    private static bool TryResolveDescentIndex(object collection, int index, out int resolved, out string error)
    {
        resolved = index;
        error = null;
        if (index >= 0)
            return true;

        if (TryReadCollectionLength(collection, collection.GetType()) is not { } length)
        {
            error = $"collection type {collection.GetType().FullName} exposes no length, "
                + "so a from-end index cannot be resolved.";
            return false;
        }

        resolved = length + index;
        if (resolved < 0)
        {
            error = $"from-end index reaches past the start (length={length}).";
            return false;
        }

        return true;
    }

    private static string FormatIndex(int index) => index < 0 ? $"^{-index}" : index.ToString();
}
