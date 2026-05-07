using System.Text.Json;
using Jiangyu.Core.Models;
using TinySerializer.Core.DataReaderWriters.Binary;
using TinySerializer.Core.Misc;

namespace Jiangyu.Core.Templates.Odin;

/// <summary>
/// Decodes a Sirenix Odin <c>SerializationData</c> binary payload into the
/// repository's <see cref="InspectedFieldNode"/> tree, so the template
/// browser can render Odin-routed fields the same way it renders
/// Unity-native fields. Read-only by design.
/// </summary>
public static class OdinPayloadDecoder
{
    /// <summary>
    /// Cap recursion to keep pathological / corrupt blobs from blowing the
    /// stack. The deepest sane Odin tree we have observed in MENACE templates
    /// is about a dozen levels; <c>64</c> leaves substantial headroom.
    /// </summary>
    private const int DefaultMaxDepth = 64;

    /// <summary>
    /// Decode a Binary-format Odin payload. Returns <c>null</c> if
    /// <paramref name="bytes"/> is empty or the decode fails before producing
    /// any nodes; otherwise returns the top-level fields of the blob.
    /// </summary>
    /// <param name="bytes">
    /// The raw bytes from <c>SerializationData.SerializedBytes</c>.
    /// </param>
    /// <param name="externalReferences">
    /// PPtr resolution for <c>ExternalReferenceByIndex</c> entries, indexed
    /// in the same order as <c>SerializationData.ReferencedUnityObjects</c>.
    /// Unresolvable / out-of-range indices land on a stub reference rather
    /// than throwing.
    /// </param>
    /// <param name="maxDepth">
    /// Optional override of the recursion cap; clamped to <c>[1, 256]</c>.
    /// </param>
    public static List<InspectedFieldNode>? DecodeBinary(
        byte[]? bytes,
        IReadOnlyList<InspectedReference?>? externalReferences = null,
        int maxDepth = DefaultMaxDepth)
    {
        if (bytes is null || bytes.Length == 0)
            return null;

        var capped = Math.Clamp(maxDepth, 1, 256);

        var stream = new MemoryStream(bytes, writable: false);
        var binder = new OdinTypeNameBinder();
        var context = new DeserializationContext { Binder = binder };
        try
        {
            using var reader = new BinaryDataReader(stream, context);
            var nodes = new List<InspectedFieldNode>();
            ReadEntries(reader, binder, externalReferences ?? [], nodes, depth: 0, capped);
            return nodes.Count == 0 ? null : nodes;
        }
        catch
        {
            // The binary reader is permissive about malformed streams (it
            // returns false from Read* and skips); a thrown exception here
            // indicates something more fundamental, such as a stream
            // truncated mid-entry-header. Surface as null so the inspect
            // pipeline keeps the raw blob alongside.
            return null;
        }
    }

    private static void ReadEntries(
        BinaryDataReader reader,
        OdinTypeNameBinder binder,
        IReadOnlyList<InspectedReference?> externalReferences,
        List<InspectedFieldNode> sink,
        int depth,
        int maxDepth,
        string? parentTypeName = null)
    {
        while (true)
        {
            var entryType = reader.PeekEntry(out var name);

            switch (entryType)
            {
                case EntryType.EndOfStream:
                case EntryType.EndOfNode:
                case EntryType.EndOfArray:
                    return;

                case EntryType.StartOfNode:
                    sink.Add(ReadNode(reader, binder, externalReferences, name, depth, maxDepth));
                    break;

                case EntryType.StartOfArray:
                    sink.Add(ReadArray(reader, binder, externalReferences, name, depth, maxDepth));
                    break;

                case EntryType.PrimitiveArray:
                    // Primitive-array entries carry only count + bytes-per-element
                    // (no element-type discriminator). The wrapping reference
                    // node's type name (e.g. "System.Int32[], mscorlib") is the
                    // sole offline source of element-type info, threaded in via
                    // parentTypeName.
                    sink.Add(ReadPrimitiveArray(reader, name, parentTypeName));
                    break;

                case EntryType.Integer:
                    sink.Add(ReadInteger(reader, name));
                    break;

                case EntryType.FloatingPoint:
                    sink.Add(ReadFloat(reader, name));
                    break;

                case EntryType.Boolean:
                    sink.Add(ReadBool(reader, name));
                    break;

                case EntryType.String:
                    sink.Add(ReadString(reader, name));
                    break;

                case EntryType.Guid:
                    sink.Add(ReadGuid(reader, name));
                    break;

                case EntryType.Null:
                    sink.Add(new InspectedFieldNode { Name = name, Kind = "null", Null = true });
                    reader.ReadNull();
                    break;

                case EntryType.InternalReference:
                    sink.Add(ReadInternalRef(reader, name));
                    break;

                case EntryType.ExternalReferenceByIndex:
                    sink.Add(ReadExternalRefByIndex(reader, externalReferences, name));
                    break;

                case EntryType.ExternalReferenceByGuid:
                    sink.Add(ReadExternalRefByGuid(reader, name));
                    break;

                case EntryType.ExternalReferenceByString:
                    sink.Add(ReadExternalRefByString(reader, name));
                    break;

                default:
                    reader.SkipEntry();
                    break;
            }
        }
    }

    private static InspectedFieldNode ReadNode(
        BinaryDataReader reader,
        OdinTypeNameBinder binder,
        IReadOnlyList<InspectedReference?> externalReferences,
        string? name,
        int depth,
        int maxDepth)
    {
        if (!reader.EnterNode(out var nodeType))
        {
            return new InspectedFieldNode { Name = name, Kind = "object", Null = true };
        }

        var node = new InspectedFieldNode
        {
            Name = name,
            Kind = "object",
            FieldTypeName = binder.TryResolveDisplayName(nodeType),
        };

        if (depth + 1 >= maxDepth)
        {
            node.Truncated = true;
            reader.ExitNode();
            return node;
        }

        var children = new List<InspectedFieldNode>();
        ReadEntries(
            reader,
            binder,
            externalReferences,
            children,
            depth + 1,
            maxDepth,
            parentTypeName: node.FieldTypeName);
        reader.ExitNode();

        // Sirenix wraps typed array/list fields in a node-with-single-anonymous-array
        // child so the wire stream can carry the array's element type. The
        // wrapper has no semantic value to a modder, just visual noise: lift
        // the array onto the parent node, preserving the wrapper's
        // FieldTypeName (which is what tells the modder it's an X[]).
        if (children.Count == 1
            && children[0] is { Name: null, Kind: "array" } innerArray)
        {
            node.Kind = "array";
            node.Count = innerArray.Count;
            node.Elements = innerArray.Elements;
            node.Truncated = innerArray.Truncated;
            node.Fields = null;
            return node;
        }

        node.Fields = children;
        return node;
    }

    private static InspectedFieldNode ReadArray(
        BinaryDataReader reader,
        OdinTypeNameBinder binder,
        IReadOnlyList<InspectedReference?> externalReferences,
        string? name,
        int depth,
        int maxDepth)
    {
        if (!reader.EnterArray(out var length))
        {
            return new InspectedFieldNode { Name = name, Kind = "array", Null = true };
        }

        var node = new InspectedFieldNode
        {
            Name = name,
            Kind = "array",
            Count = length > int.MaxValue ? int.MaxValue : (int)length,
        };

        if (depth + 1 >= maxDepth)
        {
            node.Truncated = true;
            reader.ExitArray();
            return node;
        }

        var elements = new List<InspectedFieldNode>();
        ReadEntries(reader, binder, externalReferences, elements, depth + 1, maxDepth);
        reader.ExitArray();

        // Multi-dim reshape: Sirenix Odin emits T[,] / T[,,] arrays as a
        // flat sequence prefixed by a Name="ranks" string element carrying
        // pipe-separated dimensions (e.g. "9|9"). When the array's first
        // element matches that shape AND the remaining elements all fit
        // exactly into the product of those dimensions, lift the
        // dimensions onto the node and drop the ranks prefix from
        // Elements. Consumers see a clean matrix shape ready for grid
        // rendering and cell-addressed writes.
        if (TryStripMatrixHeader(elements, out var dims, out var cells))
        {
            node.Kind = "matrix";
            node.Dimensions = dims;
            node.Count = cells.Count;
            node.Elements = cells;
            return node;
        }

        node.Elements = elements;
        return node;
    }

    private static bool TryStripMatrixHeader(
        List<InspectedFieldNode> elements,
        out List<int> dimensions,
        out List<InspectedFieldNode> cells)
    {
        dimensions = null!;
        cells = null!;
        if (elements.Count < 2) return false;

        var head = elements[0];
        if (!string.Equals(head.Name, "ranks", StringComparison.Ordinal)) return false;
        if (head.Kind != "string") return false;
        // `Value` is `object?` — when freshly produced by the decoder it's a
        // boxed string; when loaded from a serialised cache it's a
        // JsonElement of kind String. Accept both shapes.
        var ranksRaw = head.Value switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => null,
        };
        if (ranksRaw is null) return false;

        var parts = ranksRaw.Split('|');
        var parsed = new List<int>(parts.Length);
        long product = 1;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var dim) || dim < 0)
                return false;
            parsed.Add(dim);
            product *= dim;
            if (product < 0 || product > int.MaxValue)
                return false;
        }

        var expectedCells = (int)product;
        if (elements.Count - 1 != expectedCells)
            return false;

        dimensions = parsed;
        cells = elements.GetRange(1, expectedCells);
        return true;
    }

    /// <summary>
    /// Walks a deserialised field tree and applies the matrix reshape to
    /// any <c>kind=array</c> node whose first element is a Sirenix
    /// <c>"ranks"</c> header. Lets callers fix up values caches built
    /// before the reshape shipped — and is idempotent on already-reshaped
    /// data, so it's safe to call on fresh decoder output too.
    /// </summary>
    public static void ReshapeMatrices(List<InspectedFieldNode>? fields)
    {
        if (fields is null) return;
        foreach (var field in fields)
        {
            if (field.Fields is not null) ReshapeMatrices(field.Fields);
            if (field.Elements is not null) ReshapeMatrices(field.Elements);

            if (string.Equals(field.Kind, "array", StringComparison.Ordinal)
                && field.Elements is { } elements
                && TryStripMatrixHeader(elements, out var dims, out var cells))
            {
                field.Kind = "matrix";
                field.Dimensions = dims;
                field.Count = cells.Count;
                field.Elements = cells;
            }
        }
    }

    /// <summary>
    /// Decode a Sirenix <c>PrimitiveArray</c> entry. The entry only carries
    /// count + bytes-per-element + raw bytes; element-type information lives
    /// in the wrapping reference node's <see cref="Type.AssemblyQualifiedName"/>
    /// (e.g. <c>System.Int32[], mscorlib</c>). When
    /// <paramref name="parentTypeName"/> resolves to a known primitive
    /// element type we emit typed elements; otherwise we fall back to a
    /// base64-encoded raw-byte payload so nothing is lost.
    /// </summary>
    private static InspectedFieldNode ReadPrimitiveArray(
        BinaryDataReader reader,
        string? name,
        string? parentTypeName)
    {
        // We always read into a byte[] first because the typed
        // ReadPrimitiveArray<T> overload needs a known T at the call site; we
        // re-interpret the bytes ourselves once we know the element type.
        if (!reader.ReadPrimitiveArray<byte>(out var raw))
            return new InspectedFieldNode { Name = name, Kind = "primitiveArray", Reason = "read failed" };

        var elementKind = InferPrimitiveElementKind(parentTypeName);
        if (elementKind is null)
        {
            // Fallback: keep the bytes accessible via base64 so a caller can
            // recover the raw payload. Most non-int primitive arrays in
            // MENACE templates are byte sentinels for [NamedArray] enums,
            // which still benefits from byte-typed elements once we can tell.
            return new InspectedFieldNode
            {
                Name = name,
                Kind = "primitiveArray",
                Count = raw.Length,
                Value = Convert.ToBase64String(raw),
                Reason = parentTypeName is null
                    ? "no parent type to infer element type"
                    : $"unsupported primitive element type for '{parentTypeName}'",
            };
        }

        return BuildTypedPrimitiveArray(name, raw, elementKind.Value);
    }

    private enum PrimitiveElementKind
    {
        Byte, SByte, Int16, UInt16, Int32, UInt32, Int64, UInt64, Single, Double, Boolean, Char,
    }

    /// <summary>
    /// Map a wrapping reference-node type-name string of the form
    /// <c>System.Int32[], mscorlib</c> to the element kind. Returns null when
    /// the parent type isn't a recognised primitive array (e.g. a
    /// reference-typed array like <c>Foo[], Assembly-CSharp</c>; those don't
    /// reach this code path because reference arrays use StartOfArray, not
    /// PrimitiveArray, but we guard defensively).
    /// </summary>
    private static PrimitiveElementKind? InferPrimitiveElementKind(string? parentTypeName)
    {
        if (string.IsNullOrEmpty(parentTypeName)) return null;

        // Strip the assembly suffix and the "[]" array marker. Operates on
        // the AssemblyQualifiedName form Sirenix emits.
        var commaIdx = parentTypeName.IndexOf(',');
        var head = commaIdx < 0 ? parentTypeName : parentTypeName[..commaIdx];
        head = head.TrimEnd();
        if (head.EndsWith("[]", StringComparison.Ordinal))
            head = head[..^2];

        return head switch
        {
            "System.Byte" => PrimitiveElementKind.Byte,
            "System.SByte" => PrimitiveElementKind.SByte,
            "System.Int16" => PrimitiveElementKind.Int16,
            "System.UInt16" => PrimitiveElementKind.UInt16,
            "System.Int32" => PrimitiveElementKind.Int32,
            "System.UInt32" => PrimitiveElementKind.UInt32,
            "System.Int64" => PrimitiveElementKind.Int64,
            "System.UInt64" => PrimitiveElementKind.UInt64,
            "System.Single" => PrimitiveElementKind.Single,
            "System.Double" => PrimitiveElementKind.Double,
            "System.Boolean" => PrimitiveElementKind.Boolean,
            "System.Char" => PrimitiveElementKind.Char,
            _ => null,
        };
    }

    private static InspectedFieldNode BuildTypedPrimitiveArray(
        string? name,
        byte[] raw,
        PrimitiveElementKind kind)
    {
        var (stride, elementKind, valueAt) = GetElementSelectors(kind);
        if (stride == 0 || raw.Length % stride != 0)
        {
            return new InspectedFieldNode
            {
                Name = name,
                Kind = "primitiveArray",
                Count = raw.Length,
                Value = Convert.ToBase64String(raw),
                Reason = $"raw byte length {raw.Length} not a multiple of stride {stride}",
            };
        }

        var count = raw.Length / stride;
        var elements = new List<InspectedFieldNode>(count);
        for (var i = 0; i < count; i++)
        {
            elements.Add(new InspectedFieldNode
            {
                Kind = elementKind,
                Value = valueAt(raw, i * stride),
            });
        }

        return new InspectedFieldNode
        {
            Name = name,
            Kind = "array",
            Count = count,
            Elements = elements,
        };
    }

    /// <summary>
    /// Per-element-kind tuple of (byte stride, InspectedFieldNode.Kind label,
    /// reader function). Centralised so the BuildTypedPrimitiveArray loop
    /// stays a single shape.
    /// </summary>
    private static (int Stride, string Kind, Func<byte[], int, object> Read) GetElementSelectors(
        PrimitiveElementKind kind)
        => kind switch
        {
            PrimitiveElementKind.Byte => (1, "int", (b, i) => (long)b[i]),
            PrimitiveElementKind.SByte => (1, "int", (b, i) => (long)(sbyte)b[i]),
            PrimitiveElementKind.Int16 => (2, "int", (b, i) => (long)BitConverter.ToInt16(b, i)),
            PrimitiveElementKind.UInt16 => (2, "int", (b, i) => (long)BitConverter.ToUInt16(b, i)),
            PrimitiveElementKind.Int32 => (4, "int", (b, i) => (long)BitConverter.ToInt32(b, i)),
            PrimitiveElementKind.UInt32 => (4, "int", (b, i) => (long)BitConverter.ToUInt32(b, i)),
            PrimitiveElementKind.Int64 => (8, "int", (b, i) => BitConverter.ToInt64(b, i)),
            PrimitiveElementKind.UInt64 => (8, "int", (b, i) => unchecked((long)BitConverter.ToUInt64(b, i))),
            PrimitiveElementKind.Single => (4, "float", (b, i) => (double)BitConverter.ToSingle(b, i)),
            PrimitiveElementKind.Double => (8, "float", (b, i) => BitConverter.ToDouble(b, i)),
            PrimitiveElementKind.Boolean => (1, "bool", (b, i) => b[i] != 0),
            PrimitiveElementKind.Char => (2, "string", (b, i) => BitConverter.ToChar(b, i).ToString()),
            _ => (0, "primitiveArray", (_, _) => Array.Empty<byte>()),
        };

    private static InspectedFieldNode ReadInteger(BinaryDataReader reader, string? name)
    {
        if (reader.ReadInt64(out var value))
            return new InspectedFieldNode { Name = name, Kind = "int", Value = value };
        return new InspectedFieldNode { Name = name, Kind = "int", Reason = "read failed" };
    }

    private static InspectedFieldNode ReadFloat(BinaryDataReader reader, string? name)
    {
        if (reader.ReadDouble(out var value))
            return new InspectedFieldNode { Name = name, Kind = "float", Value = value };
        return new InspectedFieldNode { Name = name, Kind = "float", Reason = "read failed" };
    }

    private static InspectedFieldNode ReadBool(BinaryDataReader reader, string? name)
    {
        if (reader.ReadBoolean(out var value))
            return new InspectedFieldNode { Name = name, Kind = "bool", Value = value };
        return new InspectedFieldNode { Name = name, Kind = "bool", Reason = "read failed" };
    }

    private static InspectedFieldNode ReadString(BinaryDataReader reader, string? name)
    {
        if (reader.ReadString(out var value))
            return new InspectedFieldNode { Name = name, Kind = "string", Value = value };
        return new InspectedFieldNode { Name = name, Kind = "string", Reason = "read failed" };
    }

    private static InspectedFieldNode ReadGuid(BinaryDataReader reader, string? name)
    {
        if (reader.ReadGuid(out var value))
            return new InspectedFieldNode { Name = name, Kind = "guid", Value = value.ToString() };
        return new InspectedFieldNode { Name = name, Kind = "guid", Reason = "read failed" };
    }

    private static InspectedFieldNode ReadInternalRef(BinaryDataReader reader, string? name)
    {
        if (reader.ReadInternalReference(out var id))
            return new InspectedFieldNode { Name = name, Kind = "internalRef", Value = id };
        return new InspectedFieldNode { Name = name, Kind = "internalRef", Reason = "read failed" };
    }

    private static InspectedFieldNode ReadExternalRefByIndex(
        BinaryDataReader reader,
        IReadOnlyList<InspectedReference?> externalReferences,
        string? name)
    {
        if (!reader.ReadExternalReference(out int index))
            return new InspectedFieldNode { Name = name, Kind = "reference", Reason = "read failed" };

        var resolved = index >= 0 && index < externalReferences.Count
            ? externalReferences[index]
            : null;

        return new InspectedFieldNode
        {
            Name = name,
            Kind = "reference",
            Reference = resolved,
            Value = resolved is null ? index : null,
            Reason = resolved is null
                ? $"external reference index {index} out of range (table size {externalReferences.Count})"
                : null,
        };
    }

    private static InspectedFieldNode ReadExternalRefByGuid(BinaryDataReader reader, string? name)
    {
        if (reader.ReadExternalReference(out Guid guid))
            return new InspectedFieldNode { Name = name, Kind = "reference", Value = guid.ToString() };
        return new InspectedFieldNode { Name = name, Kind = "reference", Reason = "read failed" };
    }

    private static InspectedFieldNode ReadExternalRefByString(BinaryDataReader reader, string? name)
    {
        if (reader.ReadExternalReference(out string id))
            return new InspectedFieldNode { Name = name, Kind = "reference", Value = id };
        return new InspectedFieldNode { Name = name, Kind = "reference", Reason = "read failed" };
    }
}
