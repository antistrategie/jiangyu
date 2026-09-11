using System.Globalization;
using Il2CppInterop.Runtime.InteropTypes;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Loader.Templates;

internal sealed partial class TemplatePatchApplier
{
    internal static bool TryReadNumericPlaceholder(NumericPlaceholderBinding binding, out string text, out string error)
    {
        text = null;
        if (!TryResolveTemplateReference(binding.Source, typeof(Il2CppObjectBase), out var current, out error))
            return false;

        foreach (var segment in binding.Path.Split('.'))
        {
            if (current == null)
            {
                error = $"null before '{segment}'.";
                return false;
            }
            if (TryCastToLiveConcreteType(current, current.GetType(), out var concrete, out _))
                current = concrete;
            var bracket = segment.IndexOf('[');
            var name = bracket < 0 ? segment : segment[..bracket];
            if (!TryReadMember(current, name, out current, out _, out error))
                return false;
            if (bracket >= 0)
            {
                var index = int.Parse(segment[(bracket + 1)..^1], CultureInfo.InvariantCulture);
                if (!TryIndexInto(current, index, out current, out _, out error))
                    return false;
            }
        }
        if (binding.TryFormat(current, out text))
            return true;
        error = $"'{binding.Path}' is not a finite numeric value.";
        return false;
    }
}
