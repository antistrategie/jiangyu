using Jiangyu.Core.Models;
using Jiangyu.Core.Templates.Odin;

namespace Jiangyu.Core.Assets;

/// <summary>
/// Walks an inspect result and, for any <c>serializationData</c> node it
/// encounters, decodes the Sirenix Odin <c>SerializedBytes</c> blob and
/// surfaces the resulting fields two ways:
/// <list type="bullet">
/// <item>A nested <c>_decoded</c> child under serializationData itself,
/// preserving the full decoded tree for callers that want to inspect Odin
/// structure verbatim.</item>
/// <item>Hoisted as siblings of <c>serializationData</c> in the parent's
/// fields list, so the schema-driven member matcher in the template browser
/// (which keys by field name) finds Odin-routed values inline with
/// Unity-native ones. Names that already exist as siblings are not
/// overwritten.</item>
/// </list>
/// </summary>
/// <remarks>
/// Runs after <see cref="ManagedTypeInspectionEnricher"/>; expects the
/// <c>serializationData</c> structure produced by AssetRipper, which has the
/// shape <c>{ SerializedFormat, SerializedBytes, ReferencedUnityObjects, ... }</c>.
/// The pass is purely additive: original raw fields and the
/// <c>_decoded</c> subtree are both retained, so re-running is idempotent.
/// </remarks>
internal static class OdinPayloadEnricher
{
    /// <summary>
    /// Field name for the synthesised decoded subtree. Underscore prefix keeps
    /// it from colliding with any real field name on
    /// <c>Sirenix.Serialization.SerializationData</c>.
    /// </summary>
    public const string DecodedFieldName = "_decoded";

    public static void Enrich(List<InspectedFieldNode> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        // Top-level fields hoist into themselves: a serializationData
        // node sitting at the very top would surface its decoded contents as
        // siblings at that same top level. In practice serializationData
        // lives one level deeper (under m_Structure), and the recursive
        // descent below threads each object's own fields list through as the
        // hoist target for that level.
        VisitFields(fields, hoistTarget: fields);
    }

    private static void VisitFields(
        List<InspectedFieldNode> fields,
        List<InspectedFieldNode> hoistTarget)
    {
        // Snapshot before iterating: TryDecodeSerializationData mutates
        // hoistTarget by appending hoisted siblings, and hoistTarget may be
        // the same list we are walking. The snapshot fixes the iteration
        // bound to the pre-mutation count.
        var snapshot = fields.ToArray();
        foreach (var field in snapshot)
        {
            if (TryDecodeSerializationData(field, hoistTarget))
            {
                // The decoded subtree we just synthesised is our own output;
                // re-walking it would only do extra work and risk
                // double-hoisting if it ever contained a nested
                // serializationData (which Odin does not currently produce).
                continue;
            }

            // Object children share the parent's identity for hoisting:
            // any serializationData inside this object surfaces as a sibling
            // at this object's level.
            if (field.Fields is { Count: > 0 } children)
                VisitFields(children, children);

            // Array elements get walked for nested serializationData but
            // each element's hoistTarget is its own fields list, not the
            // surrounding array: an array element's siblings are its own
            // sub-fields, not the other array entries.
            if (field.Elements is { Count: > 0 } elements)
            {
                foreach (var element in elements)
                {
                    if (element.Fields is { Count: > 0 } elemChildren)
                        VisitFields(elemChildren, elemChildren);
                }
            }
        }
    }

    /// <returns><c>true</c> if the field was a serializationData node we
    /// decoded.</returns>
    private static bool TryDecodeSerializationData(
        InspectedFieldNode field,
        List<InspectedFieldNode> hoistTarget)
    {
        if (!IsSerializationDataNode(field))
            return false;
        if (field.Fields is null)
            return false;

        // Already decoded by an earlier pass; don't append a second copy.
        if (field.Fields.Any(f => string.Equals(f.Name, DecodedFieldName, StringComparison.Ordinal)))
            return false;

        var format = GetEnumStringValue(field.Fields, "SerializedFormat");
        if (format is null || !string.Equals(format, "Binary", StringComparison.Ordinal))
        {
            // Phase 1 only handles Binary. Editor-format (Nodes) and JSON
            // payloads stay raw until we wire those decoders.
            return false;
        }

        var bytes = ExtractByteArray(field.Fields, "SerializedBytes");
        if (bytes is null || bytes.Length == 0)
            return false;

        var externalRefs = ExtractExternalReferences(field.Fields);
        var decoded = OdinPayloadDecoder.DecodeBinary(bytes, externalRefs);
        if (decoded is null || decoded.Count == 0)
            return false;

        field.Fields.Add(new InspectedFieldNode
        {
            Name = DecodedFieldName,
            Kind = "object",
            FieldTypeName = "Odin Binary payload (decoded)",
            Fields = decoded,
        });

        HoistDecodedFields(decoded, hoistTarget);
        return true;
    }

    /// <summary>
    /// Copies each named decoded field into <paramref name="hoistTarget"/> as
    /// a sibling, so schema-driven member lookups (keyed by field name)
    /// resolve Odin-routed values without having to know about the Odin blob.
    /// Skips:
    /// <list type="bullet">
    /// <item>Unnamed nodes (positional array entries with no name make no
    /// sense as siblings).</item>
    /// <item>Names that already exist in the target. The pre-existing value
    /// wins; an Odin field with the same name as a Unity-native field would
    /// overwrite genuine Unity data, which we never want.</item>
    /// </list>
    /// </summary>
    private static void HoistDecodedFields(
        List<InspectedFieldNode> decoded,
        List<InspectedFieldNode> hoistTarget)
    {
        var existingNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existing in hoistTarget)
        {
            if (existing.Name is not null)
                existingNames.Add(existing.Name);
        }

        foreach (var decodedField in decoded)
        {
            if (decodedField.Name is null) continue;
            if (!existingNames.Add(decodedField.Name)) continue;
            hoistTarget.Add(decodedField);
        }
    }

    private static bool IsSerializationDataNode(InspectedFieldNode field)
    {
        if (!string.Equals(field.Name, "serializationData", StringComparison.Ordinal))
            return false;
        // The AssetRipper inspection sets FieldTypeName to the SerializationData
        // type for managed wrappers; tolerate either presence or absence so a
        // future change to the type-name format doesn't break the gate.
        return field.Fields is not null;
    }

    private static string? GetEnumStringValue(List<InspectedFieldNode> fields, string name)
    {
        var node = fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
        return node?.Value as string;
    }

    private static byte[]? ExtractByteArray(List<InspectedFieldNode> fields, string name)
    {
        var node = fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
        if (node?.Elements is null)
            return null;

        // Decoding a truncated byte stream produces garbage trees (the binary
        // reader will mis-frame entries past the cut). The inspect pipeline
        // truncates large arrays when the caller passes a non-zero
        // maxArraySample; the index builder uses 0 (full) so the values cache
        // is always fine, but the live CLI inspect path may hit this.
        if (node.Truncated == true)
            return null;

        var bytes = new byte[node.Elements.Count];
        for (var i = 0; i < node.Elements.Count; i++)
        {
            // The inspector reports byte values as int (Kind == "int"). Tolerate
            // any integer-shaped scalar to stay robust if the inspector ever
            // emits a tighter type (e.g. "byte").
            if (!TryReadByteValue(node.Elements[i].Value, out var b))
                return null;
            bytes[i] = b;
        }
        return bytes;
    }

    private static bool TryReadByteValue(object? boxed, out byte value)
    {
        switch (boxed)
        {
            case byte b: value = b; return true;
            case sbyte sb: value = unchecked((byte)sb); return true;
            case short s: value = unchecked((byte)s); return true;
            case int i: value = unchecked((byte)i); return true;
            case long l: value = unchecked((byte)l); return true;
            case uint ui: value = unchecked((byte)ui); return true;
            case ulong ul: value = unchecked((byte)ul); return true;
            default: value = 0; return false;
        }
    }

    private static List<InspectedReference?> ExtractExternalReferences(List<InspectedFieldNode> fields)
    {
        var node = fields.FirstOrDefault(
            f => string.Equals(f.Name, "ReferencedUnityObjects", StringComparison.Ordinal));
        if (node?.Elements is null || node.Elements.Count == 0)
            return [];

        var refs = new List<InspectedReference?>(node.Elements.Count);
        foreach (var element in node.Elements)
            refs.Add(element.Reference);
        return refs;
    }
}
