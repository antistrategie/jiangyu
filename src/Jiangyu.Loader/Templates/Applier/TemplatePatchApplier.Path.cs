using System.Reflection;

namespace Jiangyu.Loader.Templates;

// Path navigation primitives for the patch applier. Field-path strings
// are dotted member names with no bracket indexers (descent + per-op
// element index live on the directive's Descent / Index fields). Member
// reads walk the inheritance chain and surface IL2CPP-side getter
// exceptions with the unwrapped inner cause so a misidentified field
// surfaces with an actionable message.
internal sealed partial class TemplatePatchApplier
{
    private sealed class ChainEntry
    {
        public object Parent { get; set; }
        public string Name { get; set; }
        public bool ValueIsStruct { get; set; }
    }

    // The inner FieldPath is a dotted member path with no bracket indexers
    // (descent and per-op element index live on Descent / Index fields, not
    // in the path string). Empty segments would mean the path was malformed
    // by something upstream; surface as an error rather than a silent skip.
    private static bool TryParseInnerSegments(string fieldPath, out PathSegment[] segments, out string error)
    {
        var raw = fieldPath.Split('.');
        segments = new PathSegment[raw.Length];
        error = null;

        for (var i = 0; i < raw.Length; i++)
        {
            var segment = raw[i];
            if (string.IsNullOrEmpty(segment))
            {
                segments = null;
                error = $"empty segment in fieldPath '{fieldPath}'.";
                return false;
            }
            if (segment.Contains('['))
            {
                segments = null;
                error = $"unexpected bracket indexer in inner fieldPath '{segment}'; descent uses Descent steps, element index uses op.Index.";
                return false;
            }
            segments[i] = new PathSegment(segment, null);
        }

        return true;
    }

    private static bool TryReadMember(
        object instance, string name, out object value, out Type memberType, out string error)
    {
        value = null;
        memberType = null;
        error = null;

        if (instance == null)
        {
            error = "receiver is null.";
            return false;
        }

        var type = instance.GetType();
        for (var current = type; current != null; current = current.BaseType)
        {
            var property = current.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                memberType = property.PropertyType;
                try
                {
                    value = property.GetValue(instance);
                    return true;
                }
                catch (Exception ex)
                {
                    // Reflection wraps the real exception in
                    // TargetInvocationException; unwrap so the modder
                    // sees what the getter actually threw (NREs, IL2CPP
                    // null-pointer, etc.) instead of the placeholder.
                    var inner = ex.InnerException ?? ex;
                    error = $"read of property '{name}' on {type.FullName} threw: "
                        + $"{inner.GetType().Name}: {inner.Message}";
                    return false;
                }
            }

            var field = current.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null)
            {
                memberType = field.FieldType;
                try
                {
                    value = field.GetValue(instance);
                    return true;
                }
                catch (Exception ex)
                {
                    error = $"read of field '{name}' on {type.FullName} threw: {ex.Message}";
                    return false;
                }
            }
        }

        error = $"no field or property '{name}' found on {type.FullName}.";
        return false;
    }

    private static bool TryGetWritableMember(
        object instance, string name, out Type memberType, out Action<object> setter, out Func<object> getter)
    {
        memberType = null;
        setter = null;
        getter = null;

        if (instance == null)
            return false;

        var type = instance.GetType();
        for (var current = type; current != null; current = current.BaseType)
        {
            var property = current.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null && property.GetIndexParameters().Length == 0 && property.CanWrite)
            {
                memberType = property.PropertyType;
                var local = property;
                setter = value => local.SetValue(instance, value);
                if (local.CanRead)
                    getter = () => local.GetValue(instance);
                return true;
            }

            var field = current.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null && !field.IsInitOnly)
            {
                memberType = field.FieldType;
                var local = field;
                setter = value => local.SetValue(instance, value);
                getter = () => local.GetValue(instance);
                return true;
            }
        }

        return false;
    }

    private static object TryReadField(object parent, string fieldName)
        => TryReadMember(parent, fieldName, out var value, out _, out _) ? value : null;
}
