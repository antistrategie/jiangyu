using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Templates;
using Jiangyu.Core.Tests.Templates.Fixtures.Gameplay;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Tests.Templates;

public class TemplateCatalogValidatorTests
{
    private static string FixtureAssemblyPath => typeof(FixtureEntity).Assembly.Location;
    private static TemplateTypeCatalog Load() => TemplateTypeCatalog.Load(FixtureAssemblyPath);

    private sealed class RecordingLog : ILogSink
    {
        public readonly List<string> Errors = new();
        public void Info(string message) { }
        public void Msg(string message) { }
        public void Warning(string message) { }
        public void Error(string message) => Errors.Add(message);
    }

    [Fact]
    public void UnknownTemplateType_Errors()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch { TemplateType = "NopeTemplate", TemplateId = "foo" },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("NopeTemplate", log.Errors[0]);
    }

    [Fact]
    public void UnknownField_Errors()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set = [new CompiledTemplateSetOperation { Op = CompiledTemplateOp.Set, FieldPath = "Dawg", Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = 1 } }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("'Dawg' is not a field of FixtureEntity", log.Errors[0]);
    }

    [Fact]
    public void KnownField_Passes()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set = [new CompiledTemplateSetOperation { Op = CompiledTemplateOp.Set, FieldPath = "IsEnabled", Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Boolean, Boolean = true } }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void CompositeUnknownSubField_Errors()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set = [new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Set,
                    FieldPath = "Properties",
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.Composite,
                        Composite = new CompiledTemplateComposite
                        {
                            TypeName = "FixtureProperties",
                            Fields = new Dictionary<string, CompiledTemplateValue>
                            {
                                ["Unknown"] = new() { Kind = CompiledTemplateValueKind.Int32, Int32 = 1 },
                            },
                        },
                    },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("'Unknown' is not a field of FixtureProperties", log.Errors[0]);
    }

    [Fact]
    public void NamedArray_AppendRejected()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureNamedArrayHolder",
                TemplateId = "x",
                Set = [new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Append,
                    FieldPath = "Attributes",
                    Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Byte, Int32 = 5 },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("FixtureAttribute-indexed array", log.Errors[0]);
    }

    [Fact]
    public void NamedArray_SetWithoutIndex_Rejected()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureNamedArrayHolder",
                TemplateId = "x",
                Set = [new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Set,
                    FieldPath = "Attributes",
                    Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Byte, Int32 = 5 },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("requires an 'index'", log.Errors[0]);
    }

    [Fact]
    public void NamedArray_SetWithIndex_Passes()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureNamedArrayHolder",
                TemplateId = "x",
                Set = [new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Set,
                    FieldPath = "Attributes",
                    Index = 2,
                    Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Byte, Int32 = 42 },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Reference_StringValueOnConcreteField_Coerced()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var op = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Set,
            FieldPath = "ConcreteRef",
            Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.String, String = "skill.x" },
        };
        var patches = new[]
        {
            new CompiledTemplatePatch { TemplateType = "FixtureRefHolder", TemplateId = "x", Set = [op] },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
        Assert.Equal(CompiledTemplateValueKind.TemplateReference, op.Value.Kind);
        Assert.NotNull(op.Value.Reference);
        Assert.Null(op.Value.Reference!.TemplateType);
        Assert.Equal("skill.x", op.Value.Reference.TemplateId);
    }

    [Fact]
    public void Reference_StringValueOnPolymorphicField_Errors()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureRefHolder",
                TemplateId = "x",
                Set = [new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Set,
                    FieldPath = "PolymorphicRef",
                    Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.String, String = "thing.x" },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("polymorphic", log.Errors[0]);
    }

    [Fact]
    public void Reference_ExplicitMismatchedType_Errors()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureRefHolder",
                TemplateId = "x",
                Set = [new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Set,
                    FieldPath = "ConcreteRef",
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.TemplateReference,
                        Reference = new CompiledTemplateReference
                        {
                            TemplateType = "FixtureNamedArrayHolder",
                            TemplateId = "thing",
                        },
                    },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("not assignable", log.Errors[0]);
    }

    [Fact]
    public void UnknownCloneType_Errors()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var clones = new[]
        {
            new CompiledTemplateClone { TemplateType = "NopeTemplate", SourceId = "a", CloneId = "b" },
        };

        var errors = TemplateCatalogValidator.Validate(patches: null, clones, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("NopeTemplate", log.Errors[0]);
    }

    [Fact]
    public void EditorDocument_ValidatesAndNormalisesDirectiveValues()
    {
        using var catalog = Load();
        var document = new KdlEditorDocument
        {
            Nodes =
            [
                new KdlEditorNode
                {
                    Kind = KdlEditorNodeKind.Patch,
                    TemplateType = "FixtureRefHolder",
                    TemplateId = "unit.ref",
                    Directives =
                    [
                        new KdlEditorDirective
                        {
                            Op = KdlEditorOp.Set,
                            FieldPath = "ConcreteRef",
                            Value = new KdlEditorValue
                            {
                                Kind = KdlEditorValueKind.String,
                                String = "skill.alpha",
                            },
                            Line = 12,
                        },
                    ],
                },
                new KdlEditorNode
                {
                    Kind = KdlEditorNodeKind.Patch,
                    TemplateType = "FixtureNamedArrayHolder",
                    TemplateId = "unit.named",
                    Directives =
                    [
                        new KdlEditorDirective
                        {
                            Op = KdlEditorOp.Set,
                            FieldPath = "Attributes",
                            Index = 1,
                            Value = new KdlEditorValue
                            {
                                Kind = KdlEditorValueKind.Int32,
                                Int32 = 42,
                            },
                            Line = 20,
                        },
                    ],
                },
            ],
        };

        TemplateCatalogValidator.ValidateEditorDocument(document, catalog);

        Assert.Empty(document.Errors);

        KdlEditorDirective concreteRef = document.Nodes[0].Directives[0];
        Assert.Equal(KdlEditorValueKind.TemplateReference, concreteRef.Value?.Kind);
        Assert.Null(concreteRef.Value?.ReferenceType);
        Assert.Equal("skill.alpha", concreteRef.Value?.ReferenceId);

        KdlEditorDirective namedArray = document.Nodes[1].Directives[0];
        Assert.Equal(KdlEditorValueKind.Byte, namedArray.Value?.Kind);
        Assert.Equal(42, namedArray.Value?.Int32);
    }

    [Fact]
    public void EditorDocument_ReportsCollectionSetWithoutIndexOnDirectiveLine()
    {
        using var catalog = Load();
        var document = new KdlEditorDocument
        {
            Nodes =
            [
                new KdlEditorNode
                {
                    Kind = KdlEditorNodeKind.Patch,
                    TemplateType = "FixtureEntity",
                    TemplateId = "unit.x",
                    Directives =
                    [
                        new KdlEditorDirective
                        {
                            Op = KdlEditorOp.Set,
                            FieldPath = "Skills",
                            Value = new KdlEditorValue
                            {
                                Kind = KdlEditorValueKind.Int32,
                                Int32 = 1,
                            },
                            Line = 33,
                        },
                    ],
                },
            ],
        };

        TemplateCatalogValidator.ValidateEditorDocument(document, catalog);

        var error = Assert.Single(document.Errors);
        Assert.Equal(33, error.Line);
        Assert.Contains("requires an 'index' field", error.Message);
    }

    [Fact]
    public void EditorDocument_RejectsNamedArrayAppend()
    {
        using var catalog = Load();
        var document = new KdlEditorDocument
        {
            Nodes =
            [
                new KdlEditorNode
                {
                    Kind = KdlEditorNodeKind.Patch,
                    TemplateType = "FixtureNamedArrayHolder",
                    TemplateId = "unit.x",
                    Directives =
                    [
                        new KdlEditorDirective
                        {
                            Op = KdlEditorOp.Append,
                            FieldPath = "Attributes",
                            Value = new KdlEditorValue
                            {
                                Kind = KdlEditorValueKind.Int32,
                                Int32 = 7,
                            },
                            Line = 44,
                        },
                    ],
                },
            ],
        };

        TemplateCatalogValidator.ValidateEditorDocument(document, catalog);

        var error = Assert.Single(document.Errors);
        Assert.Equal(44, error.Line);
        Assert.Contains("op=Append is not supported", error.Message);
    }

    // --- Enum value validation (catches the AddItemSlot/ItemSlot mistake) ---

    private static CompiledTemplatePatch[] EnumPatch(CompiledTemplateValue value) =>
    [
        new CompiledTemplatePatch
        {
            TemplateType = "FixtureEntity",
            TemplateId = "unit.x",
            Set = [new CompiledTemplateSetOperation
            {
                Op = CompiledTemplateOp.Set,
                FieldPath = "Properties.DamageType",
                Value = value,
            }],
        },
    ];

    [Fact]
    public void Enum_MatchingTypeAndDefinedMember_Passes()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = EnumPatch(new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Enum,
            EnumType = "FixtureDamageType",
            EnumValue = "Plasma",
        });

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Enum_NoExplicitTypeAndDefinedMember_Passes()
    {
        // Editor-emitted form for monomorphic enums: type is implicit.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = EnumPatch(new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Enum,
            EnumType = null,
            EnumValue = "Ballistic",
        });

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Enum_NumericMemberValue_AcceptedWhenDefined()
    {
        // Mirrors the runtime applier's Enum.Parse numeric fallback. "1" maps
        // to Ballistic in FixtureDamageType (Blunt=0, Ballistic=1, Plasma=2).
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = EnumPatch(new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Enum,
            EnumType = "FixtureDamageType",
            EnumValue = "1",
        });

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Enum_MismatchedTypeName_Errors()
    {
        // The user's exact mistake: enum="AddItemSlot" "8" on a field declared
        // as ItemSlot. Catch at compile time before the loader sees it.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = EnumPatch(new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Enum,
            EnumType = "FixtureAttribute",
            EnumValue = "Blunt",
        });

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("FixtureAttribute", log.Errors[0]);
        Assert.Contains("FixtureDamageType", log.Errors[0]);
        // Mismatch error lists the declared members so the modder can
        // immediately see the right set instead of round-tripping back to docs.
        Assert.Contains("known members", log.Errors[0]);
        Assert.Contains("Plasma", log.Errors[0]);
    }

    [Fact]
    public void Enum_UndefinedMemberName_Errors()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = EnumPatch(new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Enum,
            EnumType = "FixtureDamageType",
            EnumValue = "Sonic",
        });

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("Sonic", log.Errors[0]);
        Assert.Contains("FixtureDamageType", log.Errors[0]);
    }

    [Fact]
    public void Enum_NumericValueOutsideDefinedSet_Errors()
    {
        // FixtureDamageType has 0/1/2; 99 isn't a defined member even though
        // it parses as a long. Match the loader's strict-membership intent.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = EnumPatch(new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Enum,
            EnumType = "FixtureDamageType",
            EnumValue = "99",
        });

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("99", log.Errors[0]);
    }

    [Fact]
    public void Enum_OnNonEnumField_Errors()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set = [new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Set,
                    FieldPath = "IsEnabled", // Boolean, not enum
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.Enum,
                        EnumType = "FixtureDamageType",
                        EnumValue = "Plasma",
                    },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("non-enum destination", log.Errors[0]);
    }

    // --- Clear ---

    [Fact]
    public void Clear_OnCollectionField_Passes()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set = [new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Clear,
                    FieldPath = "Skills",
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Clear_OnScalarField_Errors()
    {
        // Modders sometimes typo a path; pointing Clear at a scalar should
        // surface a clear error instead of silently no-opping.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set = [new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Clear,
                    FieldPath = "IsEnabled",
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("non-collection field", log.Errors[0]);
    }

    [Fact]
    public void Clear_OnNamedArray_Errors()
    {
        // NamedArray fields are fixed-size enum-indexed lookups; clearing
        // would break the slot-to-enum invariant.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureNamedArrayHolder",
                TemplateId = "x",
                Set = [new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Clear,
                    FieldPath = "Attributes",
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("FixtureAttribute-indexed array", log.Errors[0]);
    }
}
