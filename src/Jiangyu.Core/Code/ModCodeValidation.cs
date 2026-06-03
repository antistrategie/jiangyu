using Jiangyu.Core.Abstractions;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Code;

/// <summary>
/// Compile-time cross-check of <c>type="ns:Name"</c> references in emitted template
/// patches against the <c>[JiangyuType]</c> names found in the built <c>code/</c>
/// DLL. A colon marks a mod-defined type (game types are dotted Il2Cpp names with
/// no colon), so a typo'd or missing handler name fails the compile instead of
/// silently dropping at load time.
/// </summary>
public static class ModCodeValidation
{
    public static int Validate(
        IEnumerable<CompiledTemplatePatch>? patches,
        IReadOnlySet<string> knownTypeNames,
        ILogSink log)
    {
        var errors = 0;
        foreach (var (qualified, bareName) in CollectModTypeReferences(patches))
        {
            if (knownTypeNames.Contains(bareName))
                continue;

            var suggestion = ClosestName(bareName, knownTypeNames);
            var hint = suggestion is null ? "" : $" Did you mean '{suggestion}'?";
            log.Error(
                $"type=\"{qualified}\": no [JiangyuType] named '{bareName}' in the built code/ DLL.{hint} "
                + $"Defined: {(knownTypeNames.Count == 0 ? "(none — is there a code/ project?)" : string.Join(", ", knownTypeNames))}.");
            errors++;
        }

        return errors;
    }

    // Closest known type name to an unresolved reference, when near enough to be
    // a plausible typo (a casing slip or a few transposed characters), else null.
    private static string? ClosestName(string target, IReadOnlySet<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            var distance = Levenshtein(target, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        var threshold = Math.Max(2, target.Length / 3);
        return bestDistance <= threshold ? best : null;
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    private static IEnumerable<(string Qualified, string BareName)> CollectModTypeReferences(
        IEnumerable<CompiledTemplatePatch>? patches)
    {
        if (patches is null)
            yield break;

        foreach (var patch in patches)
            foreach (var reference in CompiledTemplateReferences.Enumerate(patch))
                if (reference.Kind == "type")
                    yield return (reference.Value, CodeTypeResolver.BareName(reference.Value));
    }
}
