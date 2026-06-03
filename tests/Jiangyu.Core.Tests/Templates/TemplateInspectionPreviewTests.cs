using Jiangyu.Core.Assets;
using Jiangyu.Core.Models;
using Jiangyu.Core.Templates;
using Jiangyu.Core.Abstractions;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Tests.Templates;

public sealed class TemplateInspectionPreviewTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"jiangyu-template-preview-{Guid.NewGuid():N}");

    [Fact]
    public void PreviewPlan_LoadsManifest_AndResolvesCloneChain()
    {
        Directory.CreateDirectory(_tempRoot);
        string manifestPath = Path.Combine(_tempRoot, ModManifest.FileName);
        File.WriteAllText(
            manifestPath,
            """
            {
              "name": "PreviewFixture",
              "templateClones": [
                { "templateType": "UnitLeaderTemplate", "sourceId": "base.alpha", "cloneId": "clone.alpha" },
                { "templateType": "UnitLeaderTemplate", "sourceId": "clone.alpha", "cloneId": "clone.beta" }
              ],
              "templatePatches": [
                {
                  "templateType": "UnitLeaderTemplate",
                  "templateId": "clone.beta",
                  "set": [
                    { "fieldPath": "InitialAttributes.Vitality", "value": { "kind": "Byte", "byte": 99 } }
                  ]
                }
              ]
            }
            """);

        TemplateModPreviewPlan plan = TemplateModPreviewPlan.Load(_tempRoot, NullLogSink.Instance);

        Assert.Equal(
            new TemplatePreviewKey("UnitLeaderTemplate", "base.alpha"),
            plan.ResolveUltimateSource(new TemplatePreviewKey("UnitLeaderTemplate", "clone.beta")));
        Assert.Single(plan.GetPatches(new TemplatePreviewKey("UnitLeaderTemplate", "clone.beta")));
    }

    [Fact]
    public void PreviewPlan_ResolvesACreatedTemplateToItself()
    {
        Directory.CreateDirectory(_tempRoot);
        File.WriteAllText(
            Path.Combine(_tempRoot, ModManifest.FileName),
            """
            {
              "name": "CreateFixture",
              "templateClones": [
                { "templateType": "PerkTemplate", "cloneId": "perk.fresh" },
                { "templateType": "PerkTemplate", "sourceId": "perk.fresh", "cloneId": "perk.derived" }
              ]
            }
            """);

        TemplateModPreviewPlan plan = TemplateModPreviewPlan.Load(_tempRoot, NullLogSink.Instance);

        // A 'create' (no sourceId) is its own ultimate source — not a null-id key.
        Assert.Equal(
            new TemplatePreviewKey("PerkTemplate", "perk.fresh"),
            plan.ResolveUltimateSource(new TemplatePreviewKey("PerkTemplate", "perk.fresh")));
        // A clone of a created template resolves up to the create and stops there.
        Assert.Equal(
            new TemplatePreviewKey("PerkTemplate", "perk.fresh"),
            plan.ResolveUltimateSource(new TemplatePreviewKey("PerkTemplate", "perk.derived")));
    }

    [Fact]
    public void PreviewApplier_ClonesAndAppliesScalarAndReferenceOps()
    {
        ObjectInspectionResult source = CreateUnitLeaderResult("base.alpha");
        ObjectInspectionResult cloned = TemplateInspectionPreviewApplier.CloneForTarget(
            source,
            new TemplatePreviewKey("UnitLeaderTemplate", "clone.beta"));

        TemplateInspectionPreviewApplier.Apply(
            cloned,
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "UnitLeaderTemplate",
                    TemplateId = "clone.beta",
                    Set =
                    [
                        new CompiledTemplateSetOperation
                        {
                            FieldPath = "InitialAttributes[4]",
                            Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Byte, Byte = 99 },
                        },
                        new CompiledTemplateSetOperation
                        {
                            Op = CompiledTemplateOp.Append,
                            FieldPath = "PerkTrees",
                            Value = new CompiledTemplateValue
                            {
                                Kind = CompiledTemplateValueKind.TemplateReference,
                                Reference = new CompiledTemplateReference
                                {
                                    TemplateType = "PerkTreeTemplate",
                                    TemplateId = "perk_tree.greifinger",
                                },
                            },
                        },
                    ],
                },
            ],
            reference => new TemplatePreviewResolvedReference
            {
                TemplateType = reference.TemplateType ?? string.Empty,
                TemplateId = reference.TemplateId,
                Collection = "resources.assets",
                PathId = 2002,
            });

        Assert.Equal("clone.beta", cloned.Object.Name);
        Assert.Equal(
            "clone.beta",
            cloned.Fields.Single(field => field.Name == "m_Name").Value);

        InspectedFieldNode structure = cloned.Fields.Single(field => field.Name == "m_Structure");
        InspectedFieldNode attributes = structure.Fields!.Single(field => field.Name == "InitialAttributes");
        Assert.Equal((byte)99, Assert.IsType<byte>(attributes.Elements![4].Value));

        InspectedFieldNode perkTrees = structure.Fields!.Single(field => field.Name == "PerkTrees");
        Assert.Equal(2, perkTrees.Count);
        InspectedFieldNode appended = perkTrees.Elements![1];
        Assert.Equal("reference", appended.Kind);
        Assert.Equal("PerkTreeTemplate", appended.Reference!.ClassName);
        Assert.Equal("perk_tree.greifinger", appended.Reference.Name);
    }

    [Fact]
    public void PreviewApplier_HandlesClearOnArrayField()
    {
        ObjectInspectionResult source = CreateUnitLeaderResult("base.alpha");
        ObjectInspectionResult cloned = TemplateInspectionPreviewApplier.CloneForTarget(
            source,
            new TemplatePreviewKey("UnitLeaderTemplate", "clone.beta"));

        TemplateInspectionPreviewApplier.Apply(
            cloned,
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "UnitLeaderTemplate",
                    TemplateId = "clone.beta",
                    Set =
                    [
                        new CompiledTemplateSetOperation
                        {
                            Op = CompiledTemplateOp.Clear,
                            FieldPath = "PerkTrees",
                        },
                    ],
                },
            ],
            reference => null);

        InspectedFieldNode structure = cloned.Fields.Single(field => field.Name == "m_Structure");
        InspectedFieldNode perkTrees = structure.Fields!.Single(field => field.Name == "PerkTrees");
        Assert.Equal(0, perkTrees.Count);
        Assert.NotNull(perkTrees.Elements);
        Assert.Empty(perkTrees.Elements!);
    }

    [Fact]
    public void PreviewApplier_RendersHandlerConstructedAtIndex()
    {
        // patch ... { set "EventHandlers" index=0 type="IgnoreDamage" {
        //   set "AbsorbDamagePct" 25 } }
        // compiles to a Set op with Index=0 and a TypeConstruction value.
        // The preview must render the freshly constructed handler in slot 0,
        // overwriting the reference element that was there.
        ObjectInspectionResult source = CreateResultWithEventHandlers("base.alpha", "ChangeProperty");
        ObjectInspectionResult cloned = TemplateInspectionPreviewApplier.CloneForTarget(
            source,
            new TemplatePreviewKey("PerkTemplate", "clone.beta"));

        TemplateInspectionPreviewApplier.Apply(
            cloned,
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "PerkTemplate",
                    TemplateId = "clone.beta",
                    Set =
                    [
                        new CompiledTemplateSetOperation
                        {
                            Op = CompiledTemplateOp.Set,
                            FieldPath = "EventHandlers",
                            Index = 0,
                            Value = new CompiledTemplateValue
                            {
                                Kind = CompiledTemplateValueKind.TypeConstruction,
                                TypeConstruction = new CompiledTemplateComposite
                                {
                                    TypeName = "IgnoreDamage",
                                    Operations =
                                    [
                                        new CompiledTemplateSetOperation
                                        {
                                            FieldPath = "AbsorbDamagePct",
                                            Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = 25 },
                                        },
                                    ],
                                },
                            },
                        },
                    ],
                },
            ],
            reference => null);

        InspectedFieldNode handlers = cloned.Fields
            .Single(f => f.Name == "m_Structure").Fields!
            .Single(f => f.Name == "EventHandlers");
        InspectedFieldNode replaced = handlers.Elements![0];
        Assert.Equal("object", replaced.Kind);
        Assert.Equal("IgnoreDamage", replaced.FieldTypeName);
        Assert.Equal(25, replaced.Fields!.Single(f => f.Name == "AbsorbDamagePct").Value);
    }

    [Fact]
    public void PreviewApplier_EditsElementInPlace()
    {
        // An edit descent (set "EventHandlers" index=0 { ... }) navigates into
        // the existing element and applies inner ops: node identity and other
        // fields are preserved.
        ObjectInspectionResult source = CreateResultWithEventHandlers("base.alpha", "ChangeProperty");
        InspectedFieldNode structure = source.Fields.Single(f => f.Name == "m_Structure");
        InspectedFieldNode handlersSource = structure.Fields!.Single(f => f.Name == "EventHandlers");
        handlersSource.Elements![0] = new InspectedFieldNode
        {
            Kind = "object",
            FieldTypeName = "Menace.Tactical.Skills.Effects.ChangeProperty",
            Fields =
            [
                new InspectedFieldNode { Name = "Amount", Kind = "int", FieldTypeName = "Int32", Value = 2 },
                new InspectedFieldNode { Name = "AmountMult", Kind = "float", FieldTypeName = "Single", Value = 1.5f },
            ],
        };

        ObjectInspectionResult cloned = TemplateInspectionPreviewApplier.CloneForTarget(
            source,
            new TemplatePreviewKey("PerkTemplate", "clone.beta"));

        TemplateInspectionPreviewApplier.Apply(
            cloned,
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "PerkTemplate",
                    TemplateId = "clone.beta",
                    Set =
                    [
                        new CompiledTemplateSetOperation
                        {
                            Op = CompiledTemplateOp.Set,
                            FieldPath = "Amount",
                            Descent =
                            [
                                new TemplateDescentStep { Field = "EventHandlers", Index = 0 },
                            ],
                            Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = 9 },
                        },
                    ],
                },
            ],
            reference => null);

        InspectedFieldNode handlers = cloned.Fields
            .Single(f => f.Name == "m_Structure").Fields!
            .Single(f => f.Name == "EventHandlers");
        InspectedFieldNode edited = handlers.Elements![0];
        Assert.Equal("object", edited.Kind);
        Assert.Equal(9, edited.Fields!.Single(f => f.Name == "Amount").Value);
        // The sibling field on the original element survives the edit.
        Assert.Equal(1.5f, edited.Fields!.Single(f => f.Name == "AmountMult").Value);
    }

    [Fact]
    public void PreviewApplier_ClearResetsInlineObjectFields()
    {
        // clear "AIRole" resets the inline object's scalar fields to defaults.
        ObjectInspectionResult source = CreateResultWithAIRole("base.alpha");
        ObjectInspectionResult cloned = TemplateInspectionPreviewApplier.CloneForTarget(
            source, new TemplatePreviewKey("EntityTemplate", "clone.beta"));

        TemplateInspectionPreviewApplier.Apply(
            cloned,
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "EntityTemplate",
                    TemplateId = "clone.beta",
                    Set = [new CompiledTemplateSetOperation { Op = CompiledTemplateOp.Clear, FieldPath = "AIRole" }],
                },
            ],
            reference => null);

        InspectedFieldNode aiRole = cloned.Fields
            .Single(f => f.Name == "m_Structure").Fields!
            .Single(f => f.Name == "AIRole");
        Assert.Equal(false, aiRole.Fields!.Single(f => f.Name == "AvoidOpponents").Value);
        Assert.Equal(0, aiRole.Fields!.Single(f => f.Name == "SafetyScale").Value);
    }

    [Fact]
    public void PreviewApplier_ClearThenEditComposes()
    {
        // clear "AIRole"; set "AIRole" { set "AvoidOpponents" #true } resets the
        // object then edits one field: AvoidOpponents true, the rest default.
        ObjectInspectionResult source = CreateResultWithAIRole("base.alpha");
        ObjectInspectionResult cloned = TemplateInspectionPreviewApplier.CloneForTarget(
            source, new TemplatePreviewKey("EntityTemplate", "clone.beta"));

        TemplateInspectionPreviewApplier.Apply(
            cloned,
            [
                new CompiledTemplatePatch
                {
                    TemplateType = "EntityTemplate",
                    TemplateId = "clone.beta",
                    Set =
                    [
                        new CompiledTemplateSetOperation { Op = CompiledTemplateOp.Clear, FieldPath = "AIRole" },
                        new CompiledTemplateSetOperation
                        {
                            Op = CompiledTemplateOp.Set,
                            FieldPath = "AvoidOpponents",
                            Descent = [new TemplateDescentStep { Field = "AIRole", Index = null }],
                            Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Boolean, Boolean = true },
                        },
                    ],
                },
            ],
            reference => null);

        InspectedFieldNode aiRole = cloned.Fields
            .Single(f => f.Name == "m_Structure").Fields!
            .Single(f => f.Name == "AIRole");
        Assert.Equal(true, aiRole.Fields!.Single(f => f.Name == "AvoidOpponents").Value);
        Assert.Equal(0, aiRole.Fields!.Single(f => f.Name == "SafetyScale").Value);
    }

    // A minimal EntityTemplate-shaped result whose m_Structure carries an
    // inline AIRole object with two scalar fields, mirroring how the inspector
    // renders a RoleData struct.
    private static ObjectInspectionResult CreateResultWithAIRole(string templateId)
    {
        return new ObjectInspectionResult
        {
            Object = new InspectedObjectIdentity
            {
                Name = templateId,
                ClassName = "MonoBehaviour",
                Collection = "resources.assets",
                PathId = 128003,
            },
            Options = new ObjectInspectionOptions { MaxDepth = 8, MaxArraySampleLength = 16, Truncated = false },
            Fields =
            [
                new InspectedFieldNode { Name = "m_Name", Kind = "string", FieldTypeName = "String", Value = templateId },
                new InspectedFieldNode
                {
                    Name = "m_Structure",
                    Kind = "object",
                    FieldTypeName = "Menace.Tactical.EntityTemplate",
                    Fields =
                    [
                        new InspectedFieldNode
                        {
                            Name = "AIRole",
                            Kind = "object",
                            FieldTypeName = "Menace.Tactical.AI.Data.RoleData",
                            Fields =
                            [
                                new InspectedFieldNode { Name = "AvoidOpponents", Kind = "bool", FieldTypeName = "Boolean", Value = true },
                                new InspectedFieldNode { Name = "SafetyScale", Kind = "float", FieldTypeName = "Single", Value = 1.5f },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    [Fact]
    public void TextRenderer_PreservesStableReferenceIdentity()
    {
        ObjectInspectionResult result = CreateUnitLeaderResult("clone.beta");
        string text = TemplateInspectionTextRenderer.Render(
            result,
            new TemplateInspectionTextContext
            {
                TemplateType = "UnitLeaderTemplate",
                TemplateId = "clone.beta",
                TemplateIndex = new TemplateIndex
                {
                    Classification = TemplateClassifier.GetMetadata(),
                    TemplateTypes =
                    [
                        new TemplateTypeEntry { ClassName = "PerkTreeTemplate", Count = 1, ClassifiedVia = "suffix" },
                    ],
                    Instances =
                    [
                        new TemplateInstanceEntry
                        {
                            Name = "perk_tree.darby",
                            ClassName = "PerkTreeTemplate",
                            Identity = new TemplateIdentity { Collection = "resources.assets", PathId = 1001 },
                        },
                    ],
                },
                PreviewManifestPath = "/tmp/mod/jiangyu.json",
                OdinOnlyFields = ["CustomCondition"],
            });

        Assert.Contains("preview: /tmp/mod/jiangyu.json", text);
        Assert.Contains("odin-only fields: CustomCondition", text);
        Assert.Contains("PerkTreeTemplate:perk_tree.darby", text);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static ObjectInspectionResult CreateUnitLeaderResult(string templateId)
    {
        return new ObjectInspectionResult
        {
            Object = new InspectedObjectIdentity
            {
                Name = templateId,
                ClassName = "MonoBehaviour",
                Collection = "resources.assets",
                PathId = 114691,
            },
            Options = new ObjectInspectionOptions
            {
                MaxDepth = 8,
                MaxArraySampleLength = 16,
                Truncated = false,
            },
            Fields =
            [
                new InspectedFieldNode
                {
                    Name = "m_Name",
                    Kind = "string",
                    FieldTypeName = "String",
                    Value = templateId,
                },
                new InspectedFieldNode
                {
                    Name = "m_Structure",
                    Kind = "object",
                    FieldTypeName = "Menace.Strategy.UnitLeaderTemplate",
                    Fields =
                    [
                        new InspectedFieldNode
                        {
                            Name = "InitialAttributes",
                            Kind = "array",
                            FieldTypeName = "Byte[]",
                            Count = 7,
                            Elements =
                            [
                                ByteNode(88),
                                ByteNode(80),
                                ByteNode(70),
                                ByteNode(50),
                                ByteNode(40),
                                ByteNode(20),
                                ByteNode(75),
                            ],
                        },
                        new InspectedFieldNode
                        {
                            Name = "PerkTrees",
                            Kind = "array",
                            FieldTypeName = "Menace.Strategy.PerkTreeTemplate[]",
                            Count = 1,
                            Elements =
                            [
                                new InspectedFieldNode
                                {
                                    Kind = "reference",
                                    FieldTypeName = "PerkTreeTemplate",
                                    Reference = new InspectedReference
                                    {
                                        FileId = 0,
                                        PathId = 1001,
                                        Name = "perk_tree.darby",
                                        ClassName = "MonoBehaviour",
                                    },
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    private static InspectedFieldNode ByteNode(byte value) => new()
    {
        Kind = "int",
        FieldTypeName = "Byte",
        Value = value,
    };

    // A minimal template whose m_Structure carries a polymorphic
    // EventHandlers list with a single reference element of the named handler
    // subtype, mirroring how the inspector renders SkillEventHandlerTemplate
    // entries (unexpanded PPtr references named after their concrete subtype).
    private static ObjectInspectionResult CreateResultWithEventHandlers(string templateId, string handlerSubtype)
    {
        return new ObjectInspectionResult
        {
            Object = new InspectedObjectIdentity
            {
                Name = templateId,
                ClassName = "MonoBehaviour",
                Collection = "resources.assets",
                PathId = 128002,
            },
            Options = new ObjectInspectionOptions
            {
                MaxDepth = 8,
                MaxArraySampleLength = 16,
                Truncated = false,
            },
            Fields =
            [
                new InspectedFieldNode
                {
                    Name = "m_Name",
                    Kind = "string",
                    FieldTypeName = "String",
                    Value = templateId,
                },
                new InspectedFieldNode
                {
                    Name = "m_Structure",
                    Kind = "object",
                    FieldTypeName = "Menace.Strategy.PerkTemplate",
                    Fields =
                    [
                        new InspectedFieldNode
                        {
                            Name = "EventHandlers",
                            Kind = "array",
                            FieldTypeName = "System.Collections.Generic.List`1<Menace.Tactical.Skills.SkillEventHandlerTemplate>",
                            Count = 1,
                            Elements =
                            [
                                new InspectedFieldNode
                                {
                                    Kind = "reference",
                                    FieldTypeName = "PPtr<IObject>",
                                    Reference = new InspectedReference
                                    {
                                        FileId = 0,
                                        PathId = 120862,
                                        Name = handlerSubtype,
                                        ClassName = "MonoBehaviour",
                                    },
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
