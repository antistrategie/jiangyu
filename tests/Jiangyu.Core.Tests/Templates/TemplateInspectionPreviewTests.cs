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
}
