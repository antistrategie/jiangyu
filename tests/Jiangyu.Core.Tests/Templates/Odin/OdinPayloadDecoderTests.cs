using Jiangyu.Core.Models;
using Jiangyu.Core.Templates.Odin;
using TinySerializer.Core.DataReaderWriters.Binary;
using TinySerializer.Core.Misc;

namespace Jiangyu.Core.Tests.Templates.Odin;

public class OdinPayloadDecoderTests
{
    /// <summary>
    /// Hand-build a binary blob using TinySerializer's writer with the same
    /// entry types we observe in MENACE templates, decode it, and assert the
    /// resulting tree shape. This proves the decoder handles the entry types
    /// in isolation; the real-asset round-trip (game-data-gated) covers
    /// drift between TinySerializer and Sirenix Odin in production.
    /// </summary>
    [Fact]
    public void DecodeBinary_RoundTripsScalarsAndNestedNodes()
    {
        var bytes = WriteBlob(writer =>
        {
            writer.BeginStructNode("root", typeof(SampleRoot));
            writer.WriteInt32("intField", 42);
            writer.WriteSingle("floatField", 1.5f);
            writer.WriteString("stringField", "hello");
            writer.WriteBoolean("boolField", true);
            writer.WriteNull("nullField");

            writer.BeginArrayNode(3);
            writer.WriteInt32(null, 10);
            writer.WriteInt32(null, 20);
            writer.WriteInt32(null, 30);
            writer.EndArrayNode();

            writer.BeginStructNode("nested", typeof(SampleNested));
            writer.WriteString("inner", "child");
            writer.EndNode("nested");

            writer.EndNode("root");
        });

        var fields = OdinPayloadDecoder.DecodeBinary(bytes);
        Assert.NotNull(fields);

        var root = Assert.Single(fields!);
        Assert.Equal("root", root.Name);
        Assert.Equal("object", root.Kind);
        Assert.NotNull(root.Fields);

        var intField = root.Fields!.Single(f => f.Name == "intField");
        Assert.Equal("int", intField.Kind);
        Assert.Equal(42L, intField.Value);

        var floatField = root.Fields.Single(f => f.Name == "floatField");
        Assert.Equal("float", floatField.Kind);
        Assert.Equal(1.5d, (double)floatField.Value!, 5);

        var stringField = root.Fields.Single(f => f.Name == "stringField");
        Assert.Equal("string", stringField.Kind);
        Assert.Equal("hello", stringField.Value);

        var boolField = root.Fields.Single(f => f.Name == "boolField");
        Assert.Equal("bool", boolField.Kind);
        Assert.Equal(true, boolField.Value);

        var nullField = root.Fields.Single(f => f.Name == "nullField");
        Assert.True(nullField.Null);
        Assert.Equal("null", nullField.Kind);

        // The unnamed array node is positional, surfaced with no name.
        var array = Assert.Single(root.Fields, f => f.Kind == "array");
        Assert.Equal(3, array.Count);
        Assert.NotNull(array.Elements);
        Assert.Collection(array.Elements!,
            e => Assert.Equal(10L, e.Value),
            e => Assert.Equal(20L, e.Value),
            e => Assert.Equal(30L, e.Value));

        var nested = root.Fields.Single(f => f.Name == "nested");
        Assert.Equal("object", nested.Kind);
        Assert.NotNull(nested.Fields);
        var inner = Assert.Single(nested.Fields!);
        Assert.Equal("inner", inner.Name);
        Assert.Equal("child", inner.Value);
    }

    [Fact]
    public void DecodeBinary_CapturesRawTypeNameForReferenceNodes()
    {
        var bytes = WriteBlob(writer =>
        {
            writer.BeginReferenceNode("attack", typeof(SampleRoot), id: 1);
            writer.EndNode("attack");
        });

        var fields = OdinPayloadDecoder.DecodeBinary(bytes);
        var node = Assert.Single(fields!);
        Assert.Equal("attack", node.Name);
        Assert.Equal("object", node.Kind);
        // The binder records the AssemblyQualifiedName-style string the
        // writer emitted; assert the type's short name surfaces in it.
        Assert.NotNull(node.FieldTypeName);
        Assert.Contains("SampleRoot", node.FieldTypeName);
    }

    [Fact]
    public void DecodeBinary_ResolvesExternalReferenceByIndex()
    {
        var refs = new InspectedReference?[]
        {
            new() { Name = "first", PathId = 100, ClassName = "Foo" },
            new() { Name = "second", PathId = 200, ClassName = "Bar" },
        };

        var bytes = WriteBlob(writer =>
        {
            writer.WriteExternalReference("ref0", 0);
            writer.WriteExternalReference("ref1", 1);
            writer.WriteExternalReference("dangling", 5);
        });

        var fields = OdinPayloadDecoder.DecodeBinary(bytes, refs);
        Assert.NotNull(fields);
        Assert.Equal(3, fields!.Count);

        Assert.Equal("first", fields[0].Reference?.Name);
        Assert.Equal(100, fields[0].Reference?.PathId);

        Assert.Equal("second", fields[1].Reference?.Name);

        // Out-of-range index lands on a stub with a reason.
        Assert.Null(fields[2].Reference);
        Assert.Equal(5, fields[2].Value);
        Assert.Contains("out of range", fields[2].Reason);
    }

    [Fact]
    public void DecodeBinary_ReturnsNullForEmptyInput()
    {
        Assert.Null(OdinPayloadDecoder.DecodeBinary(null));
        Assert.Null(OdinPayloadDecoder.DecodeBinary([]));
    }

    [Fact]
    public void DecodeBinary_DoesNotThrowOnGarbageInput()
    {
        var bogus = new byte[] { 0xFF, 0xFE, 0xFD, 0xFC };
        // The reader is permissive and may return null or a partial tree;
        // the contract is "no throw, no leak".
        var fields = OdinPayloadDecoder.DecodeBinary(bogus);
        // Either null or a list - both acceptable; the assertion is no
        // exception escaped.
        _ = fields;
    }

    [Fact]
    public void DecodeBinary_DecodesRealShipUpgradeSlotBlob()
    {
        // Captured from a live MENACE template (resources.assets:114237,
        // ship_upgrade_slot.armament). 100 bytes; a NamedReference node
        // wrapping a System.Int32[] whose payload comes through as a
        // PrimitiveArray entry. Pinned here so format drift between
        // TinySerializer (test-side writer) and Sirenix Odin (production
        // writer) surfaces as a failing test rather than a silent regression.
        var blob = Convert.FromBase64String(
            "AQEFAAAAUwBsAG8AdABzAC8AAAAAARgAAABTAHkAcwB0AGUAbQAuAEkAbgB0ADMAMgBbAF0ALAAgAG0AcwBjAG8AcgBsAGkAYgAAAAAACAMAAAAEAAAAAwAAAAQAAAAFAAAABQ==");

        var fields = OdinPayloadDecoder.DecodeBinary(blob);
        Assert.NotNull(fields);
        // The wrapping reference node previously surfaced as a separate
        // object-with-one-anonymous-array-child layer; the array-wrapper
        // collapse now lifts the array onto the parent, while the parent
        // keeps the typed FieldTypeName so the modder can still see the
        // System.Int32[] hint. Inner element decode (typed ints, 3/4/5)
        // verifies the parent-type-driven primitive-array path.
        var slots = Assert.Single(fields!);
        Assert.Equal("Slots", slots.Name);
        Assert.Equal("array", slots.Kind);
        Assert.NotNull(slots.FieldTypeName);
        Assert.Contains("System.Int32[]", slots.FieldTypeName);
        Assert.Equal(3, slots.Count);
        Assert.NotNull(slots.Elements);
        Assert.Collection(slots.Elements!,
            e => Assert.Equal(3L, e.Value),
            e => Assert.Equal(4L, e.Value),
            e => Assert.Equal(5L, e.Value));
    }

    [Fact]
    public void DecodeBinary_CollapsesArrayWrapperNode()
    {
        // Sirenix encodes typed array fields as a struct-node-with-one-anonymous
        // -array-child; the wrapper carries the typed-element FieldTypeName.
        // The decoder lifts the inner array up so the browser shows the array
        // directly instead of "object > unnamed array".
        var bytes = WriteBlob(w =>
        {
            w.BeginStructNode("m_ObjectiveGroups", typeof(SampleNested));
            w.BeginArrayNode(2);
            w.WriteString(null, "first");
            w.WriteString(null, "second");
            w.EndArrayNode();
            w.EndNode("m_ObjectiveGroups");
        });

        var fields = OdinPayloadDecoder.DecodeBinary(bytes);
        var node = Assert.Single(fields!);

        Assert.Equal("m_ObjectiveGroups", node.Name);
        // Lifted onto the parent: kind switches to "array" and Elements carries
        // the inner array, while FieldTypeName preserves the wrapper's typed
        // element name (e.g. "Menace.Tactical.ObjectiveGroupConfig[]").
        Assert.Equal("array", node.Kind);
        Assert.NotNull(node.FieldTypeName);
        Assert.Contains("SampleNested", node.FieldTypeName);
        Assert.Equal(2, node.Count);
        Assert.NotNull(node.Elements);
        Assert.Collection(node.Elements!,
            e => Assert.Equal("first", e.Value),
            e => Assert.Equal("second", e.Value));
    }

    [Fact]
    public void DecodeBinary_DoesNotCollapseObjectWithMultipleChildren()
    {
        // Negative guard: an object with multiple fields, even if one is an
        // anonymous array, must not be flattened.
        var bytes = WriteBlob(w =>
        {
            w.BeginStructNode("Container", typeof(SampleNested));
            w.WriteString("Tag", "real-field");
            w.BeginArrayNode(1);
            w.WriteString(null, "in-array");
            w.EndArrayNode();
            w.EndNode("Container");
        });

        var fields = OdinPayloadDecoder.DecodeBinary(bytes);
        var node = Assert.Single(fields!);
        Assert.Equal("object", node.Kind);
        Assert.NotNull(node.Fields);
        Assert.Equal(2, node.Fields!.Count);
    }

    [Fact]
    public void DecodeBinary_ReshapesMultiDimArrayAsMatrix()
    {
        // Sirenix Odin encodes T[,] as a flat sequence: a Name="ranks"
        // string carrying pipe-separated dimensions, then row-major cells.
        // The decoder reshape lifts dimensions onto the node, sets
        // Kind="matrix", and trims the header from Elements so consumers
        // see a clean grid.
        var bytes = WriteBlob(w =>
        {
            w.BeginArrayNode(5);
            w.WriteString("ranks", "2|2");
            w.WriteBoolean(null, true);
            w.WriteBoolean(null, false);
            w.WriteBoolean(null, false);
            w.WriteBoolean(null, true);
            w.EndArrayNode();
        });

        var fields = OdinPayloadDecoder.DecodeBinary(bytes);
        var node = Assert.Single(fields!);
        Assert.Equal("matrix", node.Kind);
        Assert.NotNull(node.Dimensions);
        Assert.Equal(new[] { 2, 2 }, node.Dimensions);
        Assert.Equal(4, node.Count);
        Assert.NotNull(node.Elements);
        Assert.Collection(node.Elements!,
            e => Assert.Equal(true, e.Value),
            e => Assert.Equal(false, e.Value),
            e => Assert.Equal(false, e.Value),
            e => Assert.Equal(true, e.Value));
    }

    [Fact]
    public void DecodeBinary_LeavesArrayUnreshapedWhenHeaderShapeWrong()
    {
        // No ranks header → stays as a regular array.
        var bytes = WriteBlob(w =>
        {
            w.BeginArrayNode(2);
            w.WriteBoolean(null, true);
            w.WriteBoolean(null, false);
            w.EndArrayNode();
        });

        var fields = OdinPayloadDecoder.DecodeBinary(bytes);
        var node = Assert.Single(fields!);
        Assert.Equal("array", node.Kind);
        Assert.Null(node.Dimensions);
    }

    [Fact]
    public void DecodeBinary_LeavesArrayUnreshapedWhenCellCountMismatchesProduct()
    {
        // Defensive guard: if dimensions parse cleanly but element count
        // doesn't equal the product of axes, the header isn't a real
        // ranks marker — keep the array unmolested rather than truncating
        // or padding the modder's data.
        var bytes = WriteBlob(w =>
        {
            w.BeginArrayNode(3);
            w.WriteString("ranks", "5|5"); // claims 25 cells
            w.WriteBoolean(null, true);   // but only 2 follow
            w.WriteBoolean(null, false);
            w.EndArrayNode();
        });

        var fields = OdinPayloadDecoder.DecodeBinary(bytes);
        var node = Assert.Single(fields!);
        Assert.Equal("array", node.Kind);
        Assert.Null(node.Dimensions);
    }

    [Fact]
    public void ReshapeMatrices_RewritesUnreshapedArrayInPlace()
    {
        // Simulates an old values cache that was built before the matrix
        // reshape shipped: the Odin decoder produced a kind=array node
        // with a "ranks" header still inline in Elements.
        var fields = new List<InspectedFieldNode>
        {
            new()
            {
                Name = "AOETiles",
                Kind = "array",
                FieldTypeName = "System.Boolean[,]",
                Count = 4,
                Elements =
                [
                    new() { Name = "ranks", Kind = "string", Value = "2|2" },
                    new() { Kind = "bool", Value = true },
                    new() { Kind = "bool", Value = false },
                    new() { Kind = "bool", Value = false },
                    new() { Kind = "bool", Value = true },
                ],
            },
        };

        OdinPayloadDecoder.ReshapeMatrices(fields);

        var node = Assert.Single(fields);
        Assert.Equal("matrix", node.Kind);
        Assert.Equal(new[] { 2, 2 }, node.Dimensions);
        Assert.Equal(4, node.Count);
        Assert.NotNull(node.Elements);
        Assert.Equal(4, node.Elements!.Count);
        Assert.Equal(true, node.Elements[0].Value);
    }

    [Fact]
    public void ReshapeMatrices_AcceptsJsonElementValuesFromDeserialisedCache()
    {
        // Cached values come back from disk with `Value` typed as
        // JsonElement (default System.Text.Json behaviour for object?).
        // The reshape must read the ranks string out of either a boxed
        // string or a JsonElement of kind String.
        using var doc = System.Text.Json.JsonDocument.Parse("\"3|2\"");
        var ranksValue = doc.RootElement.Clone();

        var fields = new List<InspectedFieldNode>
        {
            new()
            {
                Name = "Cells",
                Kind = "array",
                Elements =
                [
                    new() { Name = "ranks", Kind = "string", Value = ranksValue },
                    new() { Kind = "bool", Value = true },
                    new() { Kind = "bool", Value = false },
                    new() { Kind = "bool", Value = true },
                    new() { Kind = "bool", Value = false },
                    new() { Kind = "bool", Value = true },
                    new() { Kind = "bool", Value = false },
                ],
            },
        };

        OdinPayloadDecoder.ReshapeMatrices(fields);

        var node = Assert.Single(fields);
        Assert.Equal("matrix", node.Kind);
        Assert.Equal(new[] { 3, 2 }, node.Dimensions);
        Assert.Equal(6, node.Count);
        Assert.Equal(6, node.Elements!.Count);
    }

    [Fact]
    public void ReshapeMatrices_IsIdempotentOnAlreadyReshapedNodes()
    {
        var fields = new List<InspectedFieldNode>
        {
            new()
            {
                Name = "Already",
                Kind = "matrix",
                Dimensions = [2, 2],
                Count = 4,
                Elements =
                [
                    new() { Kind = "bool", Value = true },
                    new() { Kind = "bool", Value = false },
                    new() { Kind = "bool", Value = false },
                    new() { Kind = "bool", Value = true },
                ],
            },
        };

        OdinPayloadDecoder.ReshapeMatrices(fields);

        var node = Assert.Single(fields);
        Assert.Equal("matrix", node.Kind);
        Assert.Equal(new[] { 2, 2 }, node.Dimensions);
        Assert.Equal(4, node.Elements!.Count);
    }

    [Fact]
    public void ReshapeMatrices_RecursesIntoNestedFieldsAndElements()
    {
        // Multi-dim arrays nested inside an object's Fields or another
        // array's Elements must also get the reshape, not just top-level.
        var fields = new List<InspectedFieldNode>
        {
            new()
            {
                Name = "Wrapper",
                Kind = "object",
                Fields =
                [
                    new()
                    {
                        Name = "Inner",
                        Kind = "array",
                        Elements =
                        [
                            new() { Name = "ranks", Kind = "string", Value = "1|1" },
                            new() { Kind = "bool", Value = true },
                        ],
                    },
                ],
            },
        };

        OdinPayloadDecoder.ReshapeMatrices(fields);

        var inner = fields[0].Fields![0];
        Assert.Equal("matrix", inner.Kind);
        Assert.Equal(new[] { 1, 1 }, inner.Dimensions);
    }

    [Theory]
    [InlineData("Menace.Tactical.Foo, Assembly-CSharp", "Menace.Tactical.Foo")]
    [InlineData("System.Int32[], mscorlib", "System.Int32[]")]
    [InlineData("System.Collections.Generic.List`1[[System.Int32, mscorlib]], mscorlib",
                "System.Collections.Generic.List`1[[System.Int32, mscorlib]]")]
    [InlineData("Plain.Name.NoComma", "Plain.Name.NoComma")]
    [InlineData("", "")]
    public void StripAssemblySuffix_RemovesTopLevelAssemblyTail(string input, string expected)
    {
        Assert.Equal(expected, OdinTypeNameBinder.StripAssemblySuffix(input));
    }

    [Fact]
    public void DecodeBinary_StripsAssemblySuffixFromTypeNames()
    {
        var bytes = WriteBlob(w =>
        {
            w.BeginReferenceNode("root", typeof(SampleRoot), id: 1);
            w.EndNode("root");
        });

        var fields = OdinPayloadDecoder.DecodeBinary(bytes);
        var node = Assert.Single(fields!);
        // Sirenix emits AssemblyQualifiedName-style strings; we display
        // without the trailing assembly suffix.
        Assert.NotNull(node.FieldTypeName);
        Assert.DoesNotContain(",", node.FieldTypeName);
    }

    [Fact]
    public void DecodeBinary_FallsBackToBase64ForUnknownPrimitiveElementType()
    {
        // The wrapping reference node's type doesn't reach the primitive
        // array reader if no parent type info is available. We exercise the
        // fallback by constructing a payload where the primitive array
        // appears at the top level (no wrapping ref node), which is unusual
        // but exercises the fallback path deterministically.
        var bytes = WriteBlob(w =>
        {
            // Use a typed primitive array write so the entry exists; bytes-per-element comes from T.
            w.WritePrimitiveArray(new int[] { 1, 2, 3 });
        });

        var fields = OdinPayloadDecoder.DecodeBinary(bytes);
        Assert.NotNull(fields);
        var arr = Assert.Single(fields!);
        Assert.Equal("primitiveArray", arr.Kind);
        // Falls back to base64 because no parent type was threaded through.
        Assert.IsType<string>(arr.Value);
        Assert.NotNull(arr.Reason);
    }

    [Fact]
    public void DecodeBinary_TruncatesWhenDepthExceeded()
    {
        var bytes = WriteBlob(writer =>
        {
            writer.BeginStructNode("a", typeof(SampleRoot));
            writer.BeginStructNode("b", typeof(SampleRoot));
            writer.BeginStructNode("c", typeof(SampleRoot));
            writer.WriteInt32("deep", 1);
            writer.EndNode("c");
            writer.EndNode("b");
            writer.EndNode("a");
        });

        var fields = OdinPayloadDecoder.DecodeBinary(bytes, externalReferences: null, maxDepth: 2);
        Assert.NotNull(fields);
        var a = Assert.Single(fields!);
        Assert.Equal("a", a.Name);
        Assert.NotNull(a.Fields);
        var b = Assert.Single(a.Fields!);
        Assert.Equal("b", b.Name);
        Assert.True(b.Truncated);
    }

    private static byte[] WriteBlob(Action<BinaryDataWriter> author)
    {
        using var stream = new MemoryStream();
        var context = new SerializationContext { Binder = TwoWaySerializationBinder.Default };
        using (var writer = new BinaryDataWriter(stream, context))
        {
            author(writer);
            writer.FlushToStream();
        }
        return stream.ToArray();
    }

    // Empty marker types: the writer needs a Type to emit on BeginStructNode
    // / BeginReferenceNode; we only assert against the type-name string the
    // decoder recovers, never against the type's fields.
    private sealed class SampleRoot { }
    private sealed class SampleNested { }
}
