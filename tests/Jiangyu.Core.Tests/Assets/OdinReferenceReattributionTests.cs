using Jiangyu.Core.Assets;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Tests.Assets;

/// <summary>
/// Covers <see cref="TemplateIndexService.ReattributeOdinReferences"/>: the
/// post-pass that swaps the opaque <c>serializationData.ReferencedUnityObjects</c>
/// path the raw-graph walker emits for Odin-serialised references with the real
/// semantic field path recovered from the decoded value tree.
/// </summary>
public sealed class OdinReferenceReattributionTests
{
    private const string Collection = "resources.assets";
    private const long MissionPathId = 100;
    private const long CaptainPathId = 42;

    private static TemplateIdentity Id(long pathId) =>
        new() { Collection = Collection, PathId = pathId };

    private static TemplateInstanceEntry Instance(long pathId, string className, string name) =>
        new() { Name = name, ClassName = className, Identity = Id(pathId) };

    private static InspectedFieldNode Reference(string name, long pathId, string targetName) =>
        new()
        {
            Name = name,
            Kind = "reference",
            Reference = new InspectedReference { PathId = pathId, Name = targetName, ClassName = "EntityTemplate" },
        };

    private static InspectedFieldNode Array(string name, params InspectedFieldNode[] elements) =>
        new() { Name = name, Kind = "array", Count = elements.Length, Elements = [.. elements] };

    private static InspectedFieldNode Obj(string? name, params InspectedFieldNode[] fields) =>
        new() { Name = name, Kind = "object", Fields = [.. fields] };

    // serializationData -> _decoded -> m_ObjectiveGroups[0].Objectives[0].Target
    private static List<InspectedFieldNode> MissionValueTree(InspectedFieldNode target) =>
    [
        Obj(
            "serializationData",
            Obj(
                OdinPayloadEnricher.DecodedFieldName,
                Array(
                    "m_ObjectiveGroups",
                    Obj(
                        null,
                        Array("Objectives", Obj(null, target)))))),
    ];

    private static TemplateIndex IndexWith(params TemplateInstanceEntry[] instances) =>
        new()
        {
            Classification = new TemplateClassificationMetadata { RuleVersion = "test", RuleDescription = "test" },
            TemplateTypes = [],
            Instances = [.. instances],
        };

    [Theory]
    // Bare path (template with no m_Structure wrapper) and the m_Structure-
    // prefixed path the raw walker emits for DataTemplate-derived types.
    [InlineData("serializationData.ReferencedUnityObjects")]
    [InlineData("m_Structure.serializationData.ReferencedUnityObjects")]
    public void ReplacesOpaquePathWithSemanticPath(string opaquePath)
    {
        var mission = Instance(MissionPathId, "GenericMissionTemplate", "mission.pirates_high_value_target");
        mission.References =
        [
            new TemplateEdge { FieldName = opaquePath, Target = Id(CaptainPathId) },
        ];
        var captain = Instance(CaptainPathId, "EntityTemplate", "enemy.pirate_captain");
        var index = IndexWith(mission, captain);

        var values = new Dictionary<string, List<InspectedFieldNode>>
        {
            [TemplateIndex.IdentityKey(mission.Identity)] =
                MissionValueTree(Reference("Target", CaptainPathId, "enemy.pirate_captain")),
        };

        TemplateIndexService.ReattributeOdinReferences(index, values);

        var edge = Assert.Single(mission.References!);
        Assert.Equal("m_ObjectiveGroups[0].Objectives[0].Target", edge.FieldName);
        Assert.Equal(CaptainPathId, edge.Target.PathId);
        Assert.DoesNotContain(mission.References!, e => e.FieldName == opaquePath);
    }

    [Fact]
    public void RebuildsReverseIndexWithSemanticPath()
    {
        var mission = Instance(MissionPathId, "GenericMissionTemplate", "mission.pirates_high_value_target");
        mission.References =
        [
            new TemplateEdge { FieldName = "serializationData.ReferencedUnityObjects", Target = Id(CaptainPathId) },
        ];
        var captain = Instance(CaptainPathId, "EntityTemplate", "enemy.pirate_captain");
        var index = IndexWith(mission, captain);
        var values = new Dictionary<string, List<InspectedFieldNode>>
        {
            [TemplateIndex.IdentityKey(mission.Identity)] =
                MissionValueTree(Reference("Target", CaptainPathId, "enemy.pirate_captain")),
        };

        TemplateIndexService.ReattributeOdinReferences(index, values);

        var entries = index.ReferencedBy![TemplateIndex.IdentityKey(captain.Identity)];
        var entry = Assert.Single(entries);
        Assert.Equal(MissionPathId, entry.Source.PathId);
        Assert.Equal("m_ObjectiveGroups[0].Objectives[0].Target", entry.FieldName);
    }

    [Fact]
    public void KeepsOpaquePathWhenTargetNotRecoveredFromDecodedTree()
    {
        // An external reference the decoder never surfaced (e.g. a target absent
        // from the decoded subtree) must keep its raw path rather than vanish.
        var mission = Instance(MissionPathId, "GenericMissionTemplate", "mission.pirates_high_value_target");
        const long uncoveredPathId = 999;
        mission.References =
        [
            new TemplateEdge { FieldName = "serializationData.ReferencedUnityObjects", Target = Id(CaptainPathId) },
            new TemplateEdge { FieldName = "serializationData.ReferencedUnityObjects", Target = Id(uncoveredPathId) },
        ];
        var captain = Instance(CaptainPathId, "EntityTemplate", "enemy.pirate_captain");
        var other = Instance(uncoveredPathId, "EntityTemplate", "enemy.pirate_grunt");
        var index = IndexWith(mission, captain, other);
        var values = new Dictionary<string, List<InspectedFieldNode>>
        {
            [TemplateIndex.IdentityKey(mission.Identity)] =
                MissionValueTree(Reference("Target", CaptainPathId, "enemy.pirate_captain")),
        };

        TemplateIndexService.ReattributeOdinReferences(index, values);

        Assert.Contains(
            mission.References!,
            e => e.FieldName == "serializationData.ReferencedUnityObjects" && e.Target.PathId == uncoveredPathId);
        Assert.Contains(
            mission.References!,
            e => e.FieldName == "m_ObjectiveGroups[0].Objectives[0].Target" && e.Target.PathId == CaptainPathId);
    }

    [Fact]
    public void LeavesTemplatesWithoutOdinPayloadUntouched()
    {
        var entity = Instance(MissionPathId, "EntityTemplate", "entity.x");
        var existing = new TemplateEdge { FieldName = "PrimaryWeapon", Target = Id(CaptainPathId) };
        entity.References = [existing];
        var weapon = Instance(CaptainPathId, "WeaponTemplate", "weapon.y");
        var index = IndexWith(entity, weapon);

        // No serializationData node: a plain Unity-serialised value tree.
        var values = new Dictionary<string, List<InspectedFieldNode>>
        {
            [TemplateIndex.IdentityKey(entity.Identity)] = [Reference("PrimaryWeapon", CaptainPathId, "weapon.y")],
        };

        TemplateIndexService.ReattributeOdinReferences(index, values);

        Assert.Same(existing, Assert.Single(entity.References!));
    }
}
