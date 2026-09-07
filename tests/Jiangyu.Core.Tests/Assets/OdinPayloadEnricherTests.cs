using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
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

        OdinPayloadEnricher.Enrich(fields, typeResolver: null, ownerTypeName: null);

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

        OdinPayloadEnricher.Enrich(fields, typeResolver: null, ownerTypeName: null);

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

        OdinPayloadEnricher.Enrich(fields, typeResolver: null, ownerTypeName: null);

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
        OdinPayloadEnricher.Enrich(fields, typeResolver: null, ownerTypeName: null);

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

        OdinPayloadEnricher.Enrich(fields, typeResolver: null, ownerTypeName: null);
        var afterFirst = fields.Count;
        OdinPayloadEnricher.Enrich(fields, typeResolver: null, ownerTypeName: null);

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
        OdinPayloadEnricher.Enrich(fields, typeResolver: null, ownerTypeName: null);

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

        OdinPayloadEnricher.Enrich(new List<InspectedFieldNode> { structure }, typeResolver: null, ownerTypeName: null);

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

        OdinPayloadEnricher.Enrich(fields, typeResolver: null, ownerTypeName: null);

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

        OdinPayloadEnricher.Enrich(fields, typeResolver: null, ownerTypeName: null);

        var decoded = fields[0].Fields!.Single(f => f.Name == OdinPayloadEnricher.DecodedFieldName);
        var refField = Assert.Single(decoded.Fields!);
        Assert.Equal("ref", refField.Name);
        Assert.NotNull(refField.Reference);
        Assert.Equal("Trigger", refField.Reference!.Name);
        Assert.Equal(999, refField.Reference.PathId);
    }

    [Fact]
    public void Enrich_NamesOdinEnumsFromTheDecodedObjectsClass()
    {
        // Mirrors a ChangeProperty handler: the blob holds a Condition whose
        // concrete class (not the ITacticalCondition the handler field
        // declares) carries an enum scalar and an enum array. Odin wrote
        // both as integers.
        var module = new ModuleDefinition("TestModule");
        var systemEnum = new TypeDefinition("System", "Enum", TypeAttributes.Public);
        module.TopLevelTypes.Add(systemEnum);
        var checkTarget = DefineEnum(module, systemEnum, "Menace.Tactical.Skills", "CheckTarget", ("User", 0), ("Target", 1));
        var tagType = DefineEnum(module, systemEnum, "Menace.Tags", "TagType", ("VEHICLE", 47), ("LARGE", 65));
        var condition = new TypeDefinition("Menace.Tactical.Skills", "EntityWithTagsCondition", TypeAttributes.Public);
        condition.Fields.Add(new FieldDefinition("Negated", FieldAttributes.Public, module.CorLibTypeFactory.Boolean));
        condition.Fields.Add(new FieldDefinition("CheckEntity", FieldAttributes.Public, checkTarget.ToTypeSignature()));
        condition.Fields.Add(new FieldDefinition("RequiredTags", FieldAttributes.Public, new SzArrayTypeSignature(tagType.ToTypeSignature())));
        module.TopLevelTypes.Add(condition);
        // A second concrete class with a differently named enum field, so an
        // ITacticalCondition[] whose elements differ in class resolves each
        // element by its own class rather than the array's declared type.
        var sideCondition = new TypeDefinition("Menace.Tactical.Skills", "SideCondition", TypeAttributes.Public);
        sideCondition.Fields.Add(new FieldDefinition("Side", FieldAttributes.Public, checkTarget.ToTypeSignature()));
        module.TopLevelTypes.Add(sideCondition);

        // The blob names the fixture CLR types; the resolver maps those names
        // onto the hand-built definitions.
        var types = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal)
        {
            [typeof(ConditionFixture).FullName!] = condition,
            [typeof(SideConditionFixture).FullName!] = sideCondition,
            [typeof(TagFixture).FullName!] = tagType,
        };

        var blob = WriteBlob(w =>
        {
            w.BeginReferenceNode("Condition", typeof(ConditionFixture), id: 1);
            w.WriteBoolean("Negated", false);
            w.WriteInt32("CheckEntity", 1);
            w.BeginReferenceNode("RequiredTags", typeof(TagFixture[]), id: 2);
            w.BeginArrayNode(2);
            w.WriteInt32(null, 65);
            w.WriteInt32(null, 47);
            w.EndArrayNode();
            w.EndNode("RequiredTags");
            w.EndNode("Condition");
            // An interface-typed array: the element type name says nothing
            // useful, each element carries its own class.
            w.BeginReferenceNode("Conditions", typeof(object[]), id: 3);
            w.BeginArrayNode(2);
            w.BeginReferenceNode(null, typeof(SideConditionFixture), id: 4);
            w.WriteInt32("Side", 0);
            w.EndNode(null);
            w.BeginReferenceNode(null, typeof(ConditionFixture), id: 5);
            w.WriteInt32("CheckEntity", 1);
            w.EndNode(null);
            w.EndArrayNode();
            w.EndNode("Conditions");
        });
        var fields = new List<InspectedFieldNode> { BuildSerializationDataNode(format: "Binary", bytes: blob) };

        OdinPayloadEnricher.Enrich(fields, name => types.GetValueOrDefault(name), ownerTypeName: null);

        var hoisted = fields.Single(f => f.Name == "Condition");
        var checkEntity = hoisted.Fields!.Single(f => f.Name == "CheckEntity");
        Assert.Equal("enum", checkEntity.Kind);
        Assert.Equal("Target", checkEntity.Value);
        Assert.Equal("bool", hoisted.Fields!.Single(f => f.Name == "Negated").Kind);
        var tags = hoisted.Fields!.Single(f => f.Name == "RequiredTags");
        Assert.Equal("array", tags.Kind);
        Assert.Collection(
            tags.Elements!,
            e =>
            {
                Assert.Equal("enum", e.Kind);
                Assert.Equal("LARGE", e.Value);
                Assert.Equal("Menace.Tags.TagType", e.FieldTypeName);
            },
            e =>
            {
                Assert.Equal("enum", e.Kind);
                Assert.Equal("VEHICLE", e.Value);
            });
        var list = fields.Single(f => f.Name == "Conditions");
        Assert.Collection(
            list.Elements!,
            e =>
            {
                var side = e.Fields!.Single(f => f.Name == "Side");
                Assert.Equal("enum", side.Kind);
                Assert.Equal("User", side.Value);
            },
            e =>
            {
                var check = e.Fields!.Single(f => f.Name == "CheckEntity");
                Assert.Equal("enum", check.Kind);
                Assert.Equal("Target", check.Value);
            });
        // The nested _decoded subtree shares the promoted nodes.
        var decoded = fields[0].Fields!.Single(f => f.Name == OdinPayloadEnricher.DecodedFieldName);
        Assert.Same(hoisted, decoded.Fields!.Single(f => f.Name == "Condition"));
    }

    [Fact]
    public void Enrich_NamesTopLevelOdinEnumsFromTheOwnerType()
    {
        var owner = DefineOwnerWithMode(out _);

        var blob = WriteBlob(w => w.WriteInt32("Mode", 1));
        var fields = new List<InspectedFieldNode> { BuildSerializationDataNode(format: "Binary", bytes: blob) };

        OdinPayloadEnricher.Enrich(fields, name => name == "Menace.Owner" ? owner : null, ownerTypeName: "Menace.Owner");

        var hoisted = fields.Single(f => f.Name == "Mode");
        Assert.Equal("enum", hoisted.Kind);
        Assert.Equal("On", hoisted.Value);
        Assert.Equal("Menace.Mode", hoisted.FieldTypeName);
    }

    [Fact]
    public void Enrich_KeepsIntegersWhenTheClassIsUnknown()
    {
        var blob = WriteBlob(w =>
        {
            w.BeginReferenceNode("Condition", typeof(ConditionFixture), id: 1);
            w.WriteInt32("CheckEntity", 1);
            w.EndNode("Condition");
        });
        var fields = new List<InspectedFieldNode> { BuildSerializationDataNode(format: "Binary", bytes: blob) };

        OdinPayloadEnricher.Enrich(fields, _ => null, ownerTypeName: null);

        var checkEntity = fields.Single(f => f.Name == "Condition").Fields!.Single(f => f.Name == "CheckEntity");
        Assert.Equal("int", checkEntity.Kind);
        Assert.Equal(1L, checkEntity.Value);
    }

    [Theory]
    [InlineData("Menace.Tags.TagType[]", "Menace.Tags.TagType")]
    [InlineData("System.Collections.Generic.List`1[[Menace.Tags.TagType, Assembly-CSharp]]", "Menace.Tags.TagType")]
    [InlineData("System.Collections.Generic.Dictionary`2[[System.String, mscorlib],[System.Int32, mscorlib]]", null)]
    [InlineData("Menace.Tags.TagType", null)]
    [InlineData(null, null)]
    public void TryGetElementTypeName_ReadsArrayAndSingleArgumentGenericNames(string? collectionTypeName, string? expected)
    {
        Assert.Equal(expected, OdinEnumPromoter.TryGetElementTypeName(collectionTypeName));
    }

    [Fact]
    public void Enrich_UsesTheScriptClassForPayloadsUnderStructure()
    {
        // DataTemplate-derived assets keep serializationData under m_Structure,
        // which carries no class name until the managed enricher has run. The
        // script class passed in owns those fields.
        var owner = DefineOwnerWithMode(out _);
        var structure = new InspectedFieldNode
        {
            Name = "m_Structure",
            Kind = "object",
            Fields = [BuildSerializationDataNode(format: "Binary", bytes: WriteBlob(w => w.WriteInt32("Mode", 1)))],
        };

        OdinPayloadEnricher.Enrich([structure], name => name == "Menace.Owner" ? owner : null, ownerTypeName: "Menace.Owner");

        var hoisted = structure.Fields.Single(f => f.Name == "Mode");
        Assert.Equal("enum", hoisted.Kind);
        Assert.Equal("On", hoisted.Value);
    }

    [Fact]
    public void Enrich_IgnoresSimpleClassNamesAsOwners()
    {
        // Unity-tree array elements carry simple class names ("Owner", not
        // "Menace.Owner"); those must not reach the resolver, where they could
        // match an unrelated global-namespace class.
        var owner = DefineOwnerWithMode(out _);
        var element = new InspectedFieldNode
        {
            Kind = "object",
            FieldTypeName = "Owner",
            Fields = [BuildSerializationDataNode(format: "Binary", bytes: WriteBlob(w => w.WriteInt32("Mode", 1)))],
        };
        var list = new InspectedFieldNode { Name = "Items", Kind = "array", Count = 1, Elements = [element] };

        OdinPayloadEnricher.Enrich([list], _ => owner, ownerTypeName: null);

        var hoisted = element.Fields!.Single(f => f.Name == "Mode");
        Assert.Equal("int", hoisted.Kind);
        Assert.Equal(1L, hoisted.Value);
    }

    [Fact]
    public void Enrich_LeavesMatrixCellsAsIntegers()
    {
        // A T[,] of an enum decodes as an object wrapping one matrix node. The
        // Studio matrix editor keys its flags grid off integer cells, so the
        // promoter leaves them alone even when the enum resolves.
        var module = new ModuleDefinition("TestModule");
        var systemEnum = new TypeDefinition("System", "Enum", TypeAttributes.Public);
        module.TopLevelTypes.Add(systemEnum);
        var tagType = DefineEnum(module, systemEnum, "Menace.Tags", "TagType", ("VEHICLE", 47), ("LARGE", 65));
        var blob = WriteBlob(w =>
        {
            w.BeginStructNode("TileFlags", typeof(TagFixture[,]));
            w.BeginArrayNode(5);
            w.WriteString("ranks", "2|2");
            w.WriteInt32(null, 65);
            w.WriteInt32(null, 47);
            w.WriteInt32(null, 65);
            w.WriteInt32(null, 47);
            w.EndArrayNode();
            w.EndNode("TileFlags");
        });
        var fields = new List<InspectedFieldNode> { BuildSerializationDataNode(format: "Binary", bytes: blob) };

        OdinPayloadEnricher.Enrich(fields, _ => tagType, ownerTypeName: null);

        var tileFlags = fields.Single(f => f.Name == "TileFlags");
        var matrix = Assert.Single(tileFlags.Fields!);
        Assert.Equal("matrix", matrix.Kind);
        Assert.All(matrix.Elements!, cell => Assert.Equal("int", cell.Kind));
    }

    private static TypeDefinition DefineOwnerWithMode(out TypeDefinition mode)
    {
        var module = new ModuleDefinition("TestModule");
        var systemEnum = new TypeDefinition("System", "Enum", TypeAttributes.Public);
        module.TopLevelTypes.Add(systemEnum);
        mode = DefineEnum(module, systemEnum, "Menace", "Mode", ("Off", 0), ("On", 1));
        var owner = new TypeDefinition("Menace", "Owner", TypeAttributes.Public);
        owner.Fields.Add(new FieldDefinition("Mode", FieldAttributes.Public, mode.ToTypeSignature()));
        module.TopLevelTypes.Add(owner);
        return owner;
    }

    private static TypeDefinition DefineEnum(
        ModuleDefinition module,
        TypeDefinition systemEnum,
        string ns,
        string name,
        params (string Name, int Value)[] members)
    {
        var type = new TypeDefinition(ns, name, TypeAttributes.Public | TypeAttributes.Sealed) { BaseType = systemEnum };
        foreach (var (memberName, value) in members)
        {
            type.Fields.Add(new FieldDefinition(
                memberName,
                FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault,
                type.ToTypeSignature())
            {
                Constant = Constant.FromValue(value),
            });
        }
        module.TopLevelTypes.Add(type);
        return type;
    }

    private sealed class ConditionFixture { }

    private sealed class SideConditionFixture { }

    private enum TagFixture { }

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
