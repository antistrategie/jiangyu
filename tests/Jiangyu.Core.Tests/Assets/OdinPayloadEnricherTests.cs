using Jiangyu.Core.Assets;
using Jiangyu.Core.Models;
using TinySerializer.Core.DataReaderWriters.Binary;
using TinySerializer.Core.Misc;

namespace Jiangyu.Core.Tests.Assets;

/// <summary>
/// Unit-level tests for the inspect-side enricher: the decoder itself is
/// covered by <c>OdinPayloadDecoderTests</c>; here we only verify the
/// enricher's plumbing (recognising serializationData, gating on
/// SerializedFormat, attaching the synthesised _decoded subtree, idempotency,
/// truncation handling).
/// </summary>
public class OdinPayloadEnricherTests
{
    [Fact]
    public void Enrich_AttachesDecodedSubtree_ForBinarySerializationData()
    {
        var bytes = WriteBlob(w =>
        {
            w.WriteString("Greeting", "hello");
        });

        var fields = new List<InspectedFieldNode>
        {
            BuildSerializationDataNode(format: "Binary", bytes: bytes),
        };

        OdinPayloadEnricher.Enrich(fields);

        var sd = fields.Single(f => f.Name == "serializationData");
        Assert.NotNull(sd.Fields);
        var decoded = sd.Fields!.Single(f => f.Name == OdinPayloadEnricher.DecodedFieldName);
        Assert.Equal("object", decoded.Kind);
        Assert.NotNull(decoded.Fields);
        var greeting = Assert.Single(decoded.Fields!);
        Assert.Equal("Greeting", greeting.Name);
        Assert.Equal("hello", greeting.Value);
    }

    [Fact]
    public void Enrich_SkipsNonBinaryFormat()
    {
        var bytes = new byte[] { 0x01, 0x02, 0x03 };
        var fields = new List<InspectedFieldNode>
        {
            BuildSerializationDataNode(format: "Nodes", bytes: bytes),
        };

        OdinPayloadEnricher.Enrich(fields);

        var sd = Assert.Single(fields);
        Assert.DoesNotContain(sd.Fields!, f => f.Name == OdinPayloadEnricher.DecodedFieldName);
    }

    [Fact]
    public void Enrich_SkipsEmptyBlob()
    {
        var fields = new List<InspectedFieldNode>
        {
            BuildSerializationDataNode(format: "Binary", bytes: []),
        };

        OdinPayloadEnricher.Enrich(fields);

        var sd = Assert.Single(fields);
        Assert.DoesNotContain(sd.Fields!, f => f.Name == OdinPayloadEnricher.DecodedFieldName);
    }

    [Fact]
    public void Enrich_SkipsTruncatedBytesNode()
    {
        // The CLI inspect path can pass maxArraySample > 0 which truncates the
        // SerializedBytes element list. Decoding a partial byte stream
        // produces garbage; the enricher must detect Truncated and bail.
        var node = BuildSerializationDataNode(format: "Binary", bytes: WriteBlob(w => w.WriteInt32("x", 1)));
        var bytesNode = node.Fields!.Single(f => f.Name == "SerializedBytes");
        bytesNode.Truncated = true;

        var fields = new List<InspectedFieldNode> { node };
        OdinPayloadEnricher.Enrich(fields);

        Assert.DoesNotContain(node.Fields!, f => f.Name == OdinPayloadEnricher.DecodedFieldName);
    }

    [Fact]
    public void Enrich_IsIdempotent()
    {
        var bytes = WriteBlob(w => w.WriteInt32("x", 42));
        var fields = new List<InspectedFieldNode>
        {
            BuildSerializationDataNode(format: "Binary", bytes: bytes),
        };

        OdinPayloadEnricher.Enrich(fields);
        var afterFirst = fields.Count;
        OdinPayloadEnricher.Enrich(fields);

        var sd = fields.Single(f => f.Name == "serializationData");
        var decodedNodes = sd.Fields!.Where(f => f.Name == OdinPayloadEnricher.DecodedFieldName).ToList();
        // Second pass is a no-op when a _decoded subtree is already present:
        // neither the nested decoded subtree nor the hoisted siblings get
        // duplicated.
        Assert.Single(decodedNodes);
        Assert.Equal(afterFirst, fields.Count);
    }

    [Fact]
    public void Enrich_HoistsDecodedFieldsAsSiblings()
    {
        // Mirrors the production shape: m_Structure contains the Unity-native
        // fields plus a serializationData blob. After enrichment, the decoded
        // Odin fields should appear next to the native fields, not just
        // nested under serializationData._decoded.
        var blob = WriteBlob(w => w.WriteString("DamageFilterCondition", "AnyEnemy"));
        var structure = new InspectedFieldNode
        {
            Name = "m_Structure",
            Kind = "object",
            Fields = new List<InspectedFieldNode>
            {
                new() { Name = "Damage", Kind = "float", Value = 50.0 },
                BuildSerializationDataNode(format: "Binary", bytes: blob),
            },
        };

        var fields = new List<InspectedFieldNode> { structure };
        OdinPayloadEnricher.Enrich(fields);

        var siblings = structure.Fields!;
        Assert.Contains(siblings, f => f.Name == "Damage");
        Assert.Contains(siblings, f => f.Name == "serializationData");
        // The hoisted sibling.
        var hoisted = siblings.Single(f => f.Name == "DamageFilterCondition");
        Assert.Equal("AnyEnemy", hoisted.Value);
    }

    [Fact]
    public void Enrich_HoistDoesNotOverwriteUnityNativeField()
    {
        // Defensive guard: if the decoded blob has a field with the same
        // name as a Unity-native sibling, the native one wins. (Real game
        // data never collides because Odin-routed fields are exactly those
        // Unity could not serialise, but we don't want a corruption to
        // silently mask a native value.)
        var blob = WriteBlob(w => w.WriteString("Damage", "from-odin"));
        var structure = new InspectedFieldNode
        {
            Name = "m_Structure",
            Kind = "object",
            Fields = new List<InspectedFieldNode>
            {
                new() { Name = "Damage", Kind = "float", Value = 50.0 },
                BuildSerializationDataNode(format: "Binary", bytes: blob),
            },
        };

        OdinPayloadEnricher.Enrich(new List<InspectedFieldNode> { structure });

        var damage = structure.Fields!.Single(f => f.Name == "Damage");
        Assert.Equal(50.0, damage.Value);
    }

    [Fact]
    public void Enrich_RecursesIntoNestedFields()
    {
        // serializationData can appear nested under m_Structure (the canonical
        // template shape) or deeper still. The visitor recurses through both
        // Fields and Elements collections.
        var bytes = WriteBlob(w => w.WriteString("Tag", "deep"));
        var fields = new List<InspectedFieldNode>
        {
            new()
            {
                Name = "m_Structure",
                Kind = "object",
                Fields = new List<InspectedFieldNode>
                {
                    BuildSerializationDataNode(format: "Binary", bytes: bytes),
                },
            },
        };

        OdinPayloadEnricher.Enrich(fields);

        var structure = fields[0].Fields!;
        var sd = structure.Single(f => f.Name == "serializationData");
        var decoded = sd.Fields!.Single(f => f.Name == OdinPayloadEnricher.DecodedFieldName);
        Assert.Single(decoded.Fields!);
        // Hoist puts "Tag" at the m_Structure level too.
        Assert.Contains(structure, f => f.Name == "Tag");
    }

    [Fact]
    public void Enrich_ResolvesExternalReferenceIndex()
    {
        var bytes = WriteBlob(w => w.WriteExternalReference("ref", 0));

        var fields = new List<InspectedFieldNode>
        {
            BuildSerializationDataNode(
                format: "Binary",
                bytes: bytes,
                externalReferences:
                [
                    new InspectedReference { Name = "Trigger", PathId = 999, ClassName = "TriggerTemplate" },
                ]),
        };

        OdinPayloadEnricher.Enrich(fields);

        var decoded = fields[0].Fields!.Single(f => f.Name == OdinPayloadEnricher.DecodedFieldName);
        var refField = Assert.Single(decoded.Fields!);
        Assert.Equal("ref", refField.Name);
        Assert.NotNull(refField.Reference);
        Assert.Equal("Trigger", refField.Reference!.Name);
        Assert.Equal(999, refField.Reference.PathId);
    }

    private static InspectedFieldNode BuildSerializationDataNode(
        string format,
        byte[] bytes,
        IReadOnlyList<InspectedReference>? externalReferences = null)
    {
        var elements = bytes.Select(b => new InspectedFieldNode
        {
            Kind = "int",
            FieldTypeName = "Byte",
            Value = (int)b,
        }).ToList();

        var refs = (externalReferences ?? [])
            .Select(r => new InspectedFieldNode
            {
                Kind = "reference",
                Reference = r,
            })
            .ToList();

        return new InspectedFieldNode
        {
            Name = "serializationData",
            Kind = "object",
            FieldTypeName = "Sirenix.Serialization.SerializationData",
            Fields = new List<InspectedFieldNode>
            {
                new() { Name = "SerializedFormat", Kind = "enum", Value = format },
                new() { Name = "SerializedBytes", Kind = "array", Count = bytes.Length, Elements = elements },
                new() { Name = "ReferencedUnityObjects", Kind = "array", Count = refs.Count, Elements = refs },
            },
        };
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
}
