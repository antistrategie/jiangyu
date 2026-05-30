using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Models;
using Jiangyu.Core.Templates;
using Jiangyu.Core.Tests.Templates.Fixtures.Gameplay;
using Jiangyu.Shared.Replacements;
using Jiangyu.Shared.Templates;
using static Jiangyu.Core.Tests.Templates.CompiledTemplateTestHelpers;

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
        // Validator surfaces the navigator's specific message: which member
        // was missing on which type. The old generic "is not a field of X"
        // message is gone — its single fallback line erased the structural
        // detail callers actually need to fix the patch.
        Assert.Contains("Dawg", log.Errors[0]);
        Assert.Contains("FixtureEntity", log.Errors[0]);
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
                            Operations = SetOps(
                                ("Unknown", new() { Kind = CompiledTemplateValueKind.Int32, Int32 = 1 })),
                        },
                    },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        // Inner-op validator wraps the per-op message with a context prefix
        // ("composite 'Properties': ..."). The unknown-field detail is still
        // the navigator's "is not a field of FixtureProperties" output.
        Assert.Contains("composite 'Properties'", log.Errors[0]);
        Assert.Contains("Unknown", log.Errors[0]);
        Assert.Contains("FixtureProperties", log.Errors[0]);
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
    public void Enum_StringValueOnEnumField_Coerced()
    {
        // Modders shouldn't have to repeat enum= when the field's
        // declared type already pins down the enum: writing
        // `set "DamageType" "Plasma"` should compile to the same
        // CompiledTemplateValue as `set "DamageType" enum="…" "Plasma"`.
        using var catalog = Load();
        var log = new RecordingLog();
        var op = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Set,
            FieldPath = "DamageType",
            Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.String, String = "Plasma" },
        };
        var patches = new[]
        {
            new CompiledTemplatePatch { TemplateType = "FixtureProperties", TemplateId = "x", Set = [op] },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
        Assert.Equal(CompiledTemplateValueKind.Enum, op.Value.Kind);
        Assert.Equal("FixtureDamageType", op.Value.EnumType);
        Assert.Equal("Plasma", op.Value.EnumValue);
    }

    [Fact]
    public void Enum_StringValueWithUnknownMember_Errors()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureProperties",
                TemplateId = "x",
                Set = [new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Set,
                    FieldPath = "DamageType",
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.String,
                        String = "Telepathic",
                    },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("not a member of enum FixtureDamageType", log.Errors[0]);
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
    public void NormaliseForEmit_ClearsCompositeTypeOnMonomorphicDestination()
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
                            FieldPath = "Properties",
                            Value = new KdlEditorValue
                            {
                                Kind = KdlEditorValueKind.Composite,
                                CompositeType = "FixtureProperties",
                                CompositeDirectives =
                                [
                                    new KdlEditorDirective
                                    {
                                        Op = KdlEditorOp.Set,
                                        FieldPath = "Accuracy",
                                        Value = new KdlEditorValue
                                        {
                                            Kind = KdlEditorValueKind.Int32,
                                            Int32 = 5,
                                        },
                                    },
                                ],
                            },
                        },
                    ],
                },
            ],
        };

        TemplateCatalogValidator.NormaliseForEmit(document, catalog);

        var directive = document.Nodes[0].Directives[0];
        Assert.Equal(KdlEditorValueKind.Composite, directive.Value?.Kind);
        Assert.True(string.IsNullOrEmpty(directive.Value?.CompositeType));
    }

    [Fact]
    public void NormaliseForEmit_ClearsTypeWhenInnerDirectiveHasValidationError()
    {
        // Regression: when ValidateOperation surfaced an error from an
        // unrelated inner directive (e.g. a string value on an Int32 field
        // that the resolver can't coerce, which is exactly what happens
        // with Stem.ID.bankId when no IBankIdResolver is wired through),
        // NormaliseForEmit silently skipped the write-back. The outer
        // composite's redundant TypeName then survived to the serialiser,
        // producing `set "Field" type="OuterType" { ... }` on round-
        // trip even though the destination is monomorphic and the modder's
        // source had used the bare inferred form.
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
                            FieldPath = "Properties",
                            Value = new KdlEditorValue
                            {
                                Kind = KdlEditorValueKind.Composite,
                                CompositeType = typeof(FixtureProperties).FullName!,
                                CompositeDirectives =
                                [
                                    new KdlEditorDirective
                                    {
                                        Op = KdlEditorOp.Set,
                                        FieldPath = "Accuracy",
                                        Value = new KdlEditorValue
                                        {
                                            // Accuracy is Int32; the validator can't coerce a
                                            // bare string with no resolver in scope.
                                            Kind = KdlEditorValueKind.String,
                                            String = "not-a-number",
                                        },
                                    },
                                ],
                            },
                        },
                    ],
                },
            ],
        };

        TemplateCatalogValidator.NormaliseForEmit(document, catalog);

        // The inner Set has an unresolved coercion, but the outer
        // composite's monomorphic type cleanup is independent of that and
        // must still apply.
        Assert.True(string.IsNullOrEmpty(document.Nodes[0].Directives[0].Value?.CompositeType));
    }

    [Fact]
    public void NormaliseForEmit_DoesNotMutateValidationOutput()
    {
        // ValidateEditorDocument keeps the filled-in CompositeType so the
        // Studio chip can display it. NormaliseForEmit is a separate pass
        // intended only for the on-disk emit path.
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
                            FieldPath = "Properties",
                            Value = new KdlEditorValue
                            {
                                Kind = KdlEditorValueKind.Composite,
                                CompositeType = "FixtureProperties",
                                CompositeDirectives = [],
                            },
                        },
                    ],
                },
            ],
        };

        TemplateCatalogValidator.ValidateEditorDocument(document, catalog);

        Assert.Empty(document.Errors);
        Assert.Equal("FixtureProperties", document.Nodes[0].Directives[0].Value?.CompositeType);
    }

    [Fact]
    public void InferredComposite_FillsTypeFromMonomorphicDestination()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Set,
                        FieldPath = "Properties",
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.Composite,
                            Composite = new CompiledTemplateComposite
                            {
                                TypeName = string.Empty,
                                Operations =
                                [
                                    new CompiledTemplateSetOperation
                                    {
                                        Op = CompiledTemplateOp.Set,
                                        FieldPath = "Accuracy",
                                        Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = 5 },
                                    },
                                ],
                            },
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
        var filled = patches[0].Set[0].Value!.Composite!.TypeName;
        Assert.EndsWith("FixtureProperties", filled);
    }

    [Fact]
    public void InferredComposite_RejectsPolymorphicDestinationWithCandidates()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Append,
                        FieldPath = "Handlers",
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.Composite,
                            Composite = new CompiledTemplateComposite
                            {
                                TypeName = string.Empty,
                                Operations = [],
                            },
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("polymorphic destination", log.Errors[0]);
        Assert.Contains("type=", log.Errors[0]);
        Assert.Contains("FixtureConcreteDerived", log.Errors[0]);
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
        Assert.Contains("primitive scalar", log.Errors[0]);
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

    // --- HashSet ops (Phase 2e) ---

    [Fact]
    public void HashSet_Append_Passes()
    {
        // HashSet<T>.Add semantics — append is the natural opt-in syntax.
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
                    Op = CompiledTemplateOp.Append,
                    FieldPath = "SkillsRemoved",
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.TemplateReference,
                        Reference = new CompiledTemplateReference { TemplateId = "skill.test" },
                    },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);
        Assert.Equal(0, errors);
    }

    [Fact]
    public void HashSet_RemoveByValue_Passes()
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
                    Op = CompiledTemplateOp.Remove,
                    FieldPath = "SkillsRemoved",
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.TemplateReference,
                        Reference = new CompiledTemplateReference { TemplateId = "skill.test" },
                    },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);
        Assert.Equal(0, errors);
    }

    [Fact]
    public void HashSet_Clear_Passes()
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
                    FieldPath = "SkillsRemoved",
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);
        Assert.Equal(0, errors);
    }

    [Fact]
    public void HashSet_InsertAt_Errors()
    {
        // HashSet has no positional order, so InsertAt is nonsensical.
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
                    Op = CompiledTemplateOp.InsertAt,
                    FieldPath = "SkillsRemoved",
                    Index = 0,
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.TemplateReference,
                        Reference = new CompiledTemplateReference { TemplateId = "skill.test" },
                    },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);
        Assert.Equal(1, errors);
        Assert.Contains("HashSet", log.Errors[0]);
        Assert.Contains("InsertAt", log.Errors[0]);
    }

    [Fact]
    public void HashSet_RemoveByIndex_Errors()
    {
        // HashSet has no positional addressing — Remove must use a value.
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
                    Op = CompiledTemplateOp.Remove,
                    FieldPath = "SkillsRemoved",
                    Index = 0,
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);
        Assert.Equal(1, errors);
        Assert.Contains("HashSet", log.Errors[0]);
    }

    [Fact]
    public void HashSet_SetWithIndex_Errors()
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
                    FieldPath = "SkillsRemoved",
                    Index = 0,
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.TemplateReference,
                        Reference = new CompiledTemplateReference { TemplateId = "skill.test" },
                    },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);
        Assert.Equal(1, errors);
        Assert.Contains("HashSet", log.Errors[0]);
    }

    [Fact]
    public void List_RemoveByValue_Errors()
    {
        // List<T> Remove still requires index — by-value is HashSet only.
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
                    Op = CompiledTemplateOp.Remove,
                    FieldPath = "Skills",
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.TemplateReference,
                        Reference = new CompiledTemplateReference { TemplateId = "skill.test" },
                    },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);
        Assert.Equal(1, errors);
        Assert.Contains("List", log.Errors[0]);
    }

    // --- Polymorphic edit descent (subtype-union) validator dispatch ---

    [Fact]
    public void PolymorphicDescent_ResolvesInnerFieldViaSubtypeUnion()
    {
        // An edit descent names no subtype: the inner field resolves against
        // the union of the element base's concrete subtypes (DerivedField lives
        // on a subtype). The runtime casts to the live element's actual type,
        // so this is accepted at compile.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Set,
                        FieldPath = "DerivedField",
                        Descent =
                        [
                            new TemplateDescentStep { Field = "Handlers", Index = 0 },
                        ],
                        Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = 7 },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
    }

    [Fact]
    public void PolymorphicDescent_BogusInnerField_Errors()
    {
        // A field that exists on no concrete subtype is a genuine typo and is
        // rejected.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Set,
                        FieldPath = "NotARealFieldOnAnySubtype",
                        Descent =
                        [
                            new TemplateDescentStep { Field = "Handlers", Index = 0 },
                        ],
                        Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = 7 },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains(log.Errors, e => e.Contains("not found on any concrete subtype", StringComparison.OrdinalIgnoreCase));
    }

    // --- Object-field edit descent (set "Field" { ... }, null index) ---

    [Fact]
    public void ObjectDescent_MonomorphicObjectField_ResolvesInnerField()
    {
        // set "Properties" { set "Accuracy" 80 } edits the Properties object in
        // place. The null-index descent step resolves the inner field on the
        // monomorphic object type directly.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Set,
                        FieldPath = "Accuracy",
                        Descent = [new TemplateDescentStep { Field = "Properties", Index = null }],
                        Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = 80 },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
    }

    [Fact]
    public void ObjectDescent_PolymorphicScalarField_ResolvesViaSubtypeUnion()
    {
        // set "AoEShape" { set "Radius" 3 } edits the live AoEShape in place.
        // AoEShape is IFixtureAoEShape (polymorphic); the null-index descent
        // resolves Radius against the base's concrete-subtype union.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Set,
                        FieldPath = "Radius",
                        Descent = [new TemplateDescentStep { Field = "AoEShape", Index = null }],
                        Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = 3 },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
    }

    [Fact]
    public void Clear_OnInlineObjectField_Validates()
    {
        // clear "Properties" resets an inline object field to a fresh default.
        // Allowed on a non-collection inline object, unlike a primitive scalar
        // or a PPtr reference.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set = [new CompiledTemplateSetOperation { Op = CompiledTemplateOp.Clear, FieldPath = "Properties" }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
    }

    [Fact]
    public void Clear_OnPolymorphicInlineField_Errors()
    {
        // clear on a polymorphic inline field (an interface or an abstract
        // class) has no single concrete default to reset to: the applier would
        // fail to construct the base or discard the live subtype. The validator
        // rejects, pointing the modder at type= reconstruction.
        using var catalog = Load();
        foreach (var field in new[] { "AoEShape", "CustomCondition" })
        {
            var log = new RecordingLog();
            var patches = new[]
            {
                new CompiledTemplatePatch
                {
                    TemplateType = "FixtureEntity",
                    TemplateId = "unit.x",
                    Set = [new CompiledTemplateSetOperation { Op = CompiledTemplateOp.Clear, FieldPath = field }],
                },
            };

            var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

            Assert.Equal(1, errors);
            Assert.Contains(log.Errors, e => e.Contains("polymorphic", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ElementEditDescent_IntoStructElement_Errors()
    {
        // set "Points" index=0 { set "X" 5 } edits a value-type (struct)
        // element in place. The runtime has no write-back path for in-collection
        // structs, so the validator rejects it rather than letting the offline
        // preview (which would apply it) diverge from the live applier (which
        // rejects it).
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureStructListHolder",
                TemplateId = "x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Set,
                        FieldPath = "X",
                        Descent = [new TemplateDescentStep { Field = "Points", Index = 0 }],
                        Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = 5 },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains(log.Errors, e => e.Contains("value-type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Clear_OnReferenceField_Errors()
    {
        // ScriptableRef is a PPtr to a ScriptableObject. clear doesn't apply —
        // null a reference with #null instead.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set = [new CompiledTemplateSetOperation { Op = CompiledTemplateOp.Clear, FieldPath = "ScriptableRef" }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains(log.Errors, e => e.Contains("#null", StringComparison.OrdinalIgnoreCase));
    }

    // --- Handler construction (slice 4b) ---

    [Fact]
    public void TypeConstruction_AppendValidates()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Append,
                        FieldPath = "Handlers",
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.TypeConstruction,
                            TypeConstruction = new CompiledTemplateComposite
                            {
                                TypeName = "FixtureConcreteDerived",
                                Operations = SetOps(
                                    ("DerivedField", new CompiledTemplateValue
                                    {
                                        Kind = CompiledTemplateValueKind.Int32,
                                        Int32 = 42,
                                    })),
                            },
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void TypeConstruction_IndexedDescentInner_Errors()
    {
        // type="FixtureConcreteDerived" { set "X" index=0 { ... } } — an indexed
        // element edit inside a freshly-constructed value. The construct's
        // collections start empty, so the inner descent can never bind at apply.
        // Rejected at compile time instead of failing at apply (and silently
        // misrendering in the preview).
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Append,
                        FieldPath = "Handlers",
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.TypeConstruction,
                            TypeConstruction = new CompiledTemplateComposite
                            {
                                TypeName = "FixtureConcreteDerived",
                                Operations =
                                [
                                    new CompiledTemplateSetOperation
                                    {
                                        Op = CompiledTemplateOp.Set,
                                        FieldPath = "DerivedField",
                                        Descent = [new TemplateDescentStep { Field = "DerivedField", Index = 0 }],
                                        Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = 1 },
                                    },
                                ],
                            },
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains(log.Errors, e => e.Contains("freshly-constructed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TypeConstruction_IndexedDescentInner_WithFrom_Allowed()
    {
        // With from= the construction deep-copies a prototype whose collections
        // may already hold elements, so an indexed element edit descent can
        // bind. The freshly-constructed guard must not fire on this case.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Append,
                        FieldPath = "Handlers",
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.TypeConstruction,
                            TypeConstruction = new CompiledTemplateComposite
                            {
                                TypeName = "FixtureConcreteDerived",
                                From = "prototype_element",
                                Operations =
                                [
                                    new CompiledTemplateSetOperation
                                    {
                                        Op = CompiledTemplateOp.Set,
                                        FieldPath = "Uses",
                                        Descent = [new TemplateDescentStep { Field = "Items", Index = 0 }],
                                        Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = 5 },
                                    },
                                ],
                            },
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void TypeConstruction_RejectsNonSubtype()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Append,
                        FieldPath = "Handlers",
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.TypeConstruction,
                            TypeConstruction = new CompiledTemplateComposite
                            {
                                TypeName = "FixtureRefHolder",
                                Operations = [],
                            },
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains(log.Errors, e => e.Contains("not a subtype", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TypeConstruction_RejectsUnknownSubtypeName()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Append,
                        FieldPath = "Handlers",
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.TypeConstruction,
                            TypeConstruction = new CompiledTemplateComposite
                            {
                                TypeName = "NoSuchType",
                                Operations = [],
                            },
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains(log.Errors, e => e.Contains("NoSuchType", StringComparison.Ordinal));
    }

    [Fact]
    public void TypeConstruction_RejectsUnknownInnerField()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Append,
                        FieldPath = "Handlers",
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.TypeConstruction,
                            TypeConstruction = new CompiledTemplateComposite
                            {
                                TypeName = "FixtureConcreteDerived",
                                Operations = SetOps(
                                    ("NotARealField", new CompiledTemplateValue
                                    {
                                        Kind = CompiledTemplateValueKind.Int32,
                                        Int32 = 1,
                                    })),
                            },
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains(log.Errors, e => e.Contains("NotARealField", StringComparison.Ordinal));
    }

    [Fact]
    public void TypeConstruction_SetOnMonomorphicScalarField_Errors()
    {
        // InitialSkill is a FixtureSkillTemplate scalar with no subclasses, so
        // a set with type= has nothing to pick. Rejected: edit in place with
        // set "InitialSkill" { ... }, or reset with clear. Polymorphic-scalar
        // destinations keep the construction path, exercised separately by
        // TypeConstruction_AcceptsOnPolymorphicScalarField.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Set,
                        FieldPath = "InitialSkill",
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.TypeConstruction,
                            TypeConstruction = new CompiledTemplateComposite
                            {
                                TypeName = "FixtureSkillTemplate",
                                Operations = [],
                            },
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains(log.Errors, e => e.Contains("monomorphic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TypeConstruction_SetIndexOnMonomorphicElement_Errors()
    {
        // Skills is List<FixtureSkillTemplate> (monomorphic element). Replacing
        // element 0 with type= has nothing to pick, so it is rejected — replace
        // via remove + insert instead. append/insert still construct an element.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Set,
                        FieldPath = "Skills",
                        Index = 0,
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.TypeConstruction,
                            TypeConstruction = new CompiledTemplateComposite { TypeName = "FixtureSkillTemplate", Operations = [] },
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains(log.Errors, e => e.Contains("monomorphic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TypeConstruction_AppendsOdinRoutedListElement()
    {
        // Phase 2c: appending a polymorphic non-ScriptableObject element to
        // an Odin-routed reference array (FixtureEntity.AoEShapes is
        // IFixtureAoEShape[]). The applier's TryConstructComposite handles
        // plain managed classes alongside ScriptableObjects, so the
        // validator must accept the Append shape symmetrically.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Append,
                        FieldPath = "AoEShapes",
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.TypeConstruction,
                            TypeConstruction = new CompiledTemplateComposite
                            {
                                TypeName = "FixtureAoEShapeImpl",
                                Operations = SetOps(
                                    ("Radius", new CompiledTemplateValue
                                    {
                                        Kind = CompiledTemplateValueKind.Int32,
                                        Int32 = 7,
                                    })),
                            },
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void TypeConstruction_RejectsEmptyTypeNameOnPolymorphicScalar()
    {
        // Polymorphic scalar destination always needs a TypeName because the
        // gate above already confirmed it has subtypes the modder must pick.
        // Without one, the runtime applier would synthesise the abstract
        // base name and crash on construction; rejecting at validate time
        // keeps the failure obvious.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Set,
                        FieldPath = "AoEShape",
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.TypeConstruction,
                            TypeConstruction = new CompiledTemplateComposite
                            {
                                TypeName = "",
                                Operations = [],
                            },
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains(log.Errors, e => e.Contains("must name a concrete subtype", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TypeConstruction_AcceptsOnPolymorphicScalarField()
    {
        // FixtureEntity.AoEShape is IFixtureAoEShape scalar with concrete
        // implementations. type="<Subtype>" on a Set must be accepted so
        // the modder can construct an Odin-routed condition like
        // Attack.DamageFilterCondition: ITacticalCondition without writing
        // to a collection slot.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Set,
                        FieldPath = "AoEShape",
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.TypeConstruction,
                            TypeConstruction = new CompiledTemplateComposite
                            {
                                TypeName = "FixtureAoEShapeImpl",
                                Operations = SetOps(
                                    ("Radius", new CompiledTemplateValue
                                    {
                                        Kind = CompiledTemplateValueKind.Int32,
                                        Int32 = 4,
                                    })),
                            },
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void EditDescent_OnNonPolymorphicCollection_Validates()
    {
        // Skills is List<FixtureSkillTemplate> where FixtureSkillTemplate has
        // no subclasses. An edit descent into a monomorphic collection resolves
        // its inner field against the concrete element type directly.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Set,
                        FieldPath = "Cooldown",
                        Descent =
                        [
                            new TemplateDescentStep { Field = "Skills", Index = 0 },
                        ],
                        Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Single, Single = 1.0f },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
    }

    [Fact]
    public void EmptyDescent_OnScalarWrite_IsIgnored()
    {
        // An explicitly empty (non-null) Descent on a scalar write is treated
        // the same as no descent — the validator must not trip over it.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Set,
                        FieldPath = "HudYOffsetScale",
                        Descent = [],
                        Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Single, Single = 1.0f },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
    }

    [Fact]
    public void TypeConstruction_OptionalSubtypeWhenMonomorphic()
    {
        // FixtureSkillTemplate has no subclasses in the fixture assembly, so
        // append "Skills" type="" {...} should resolve implicitly (we
        // simulate by passing an empty subtype name).
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Append,
                        FieldPath = "Skills",
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.TypeConstruction,
                            TypeConstruction = new CompiledTemplateComposite
                            {
                                TypeName = "",
                                Operations = SetOps(
                                    ("Uses", new CompiledTemplateValue
                                    {
                                        Kind = CompiledTemplateValueKind.Int32,
                                        Int32 = 5,
                                    })),
                            },
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void PolymorphicDescent_InnerFieldOnAnySubtype_Resolves()
    {
        // The subtype union spans every concrete subtype: DerivedFieldB lives
        // on FixtureConcreteDerivedB (a different subtype than the one carrying
        // DerivedField), and an edit descent still resolves it. The runtime
        // casts to the live element's actual type, which is authoritative.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureEntity",
                TemplateId = "unit.x",
                Set =
                [
                    new CompiledTemplateSetOperation
                    {
                        Op = CompiledTemplateOp.Set,
                        FieldPath = "DerivedFieldB",
                        Descent =
                        [
                            new TemplateDescentStep { Field = "Handlers", Index = 0 },
                        ],
                        Value = new CompiledTemplateValue { Kind = CompiledTemplateValueKind.String, String = "x" },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
    }

    [Theory]
    [InlineData("Icon")]
    [InlineData("Album")]
    [InlineData("Bark")]
    [InlineData("Skin")]
    [InlineData("Prefab")]
    public void AssetReference_OnSupportedUnityField_Passes(string fieldPath)
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureAssetHolder",
                TemplateId = "x",
                Set = [new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Set,
                    FieldPath = fieldPath,
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.AssetReference,
                        Asset = new CompiledAssetReference { Name = "item/sample" },
                    },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
    }

    [Theory]
    [InlineData("Geometry")]
    public void AssetReference_OnMeshField_PointsAtPrefabAdditionWorkflow(string fieldPath)
    {
        // Mesh fields aren't supported as asset-reference destinations; the
        // diagnostic should redirect modders to the prefab-addition workflow.
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureAssetHolder",
                TemplateId = "x",
                Set = [new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Set,
                    FieldPath = fieldPath,
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.AssetReference,
                        Asset = new CompiledAssetReference { Name = "item/sample" },
                    },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("prefab addition", log.Errors[0]);
        Assert.Contains("SkinnedMeshRenderer", log.Errors[0]);
    }

    [Fact]
    public void AssetReference_OnNonAssetField_Errors()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureAssetHolder",
                TemplateId = "x",
                Set = [new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Set,
                    FieldPath = "NotAnAsset",
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.AssetReference,
                        Asset = new CompiledAssetReference { Name = "item/sample" },
                    },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("non-asset", log.Errors[0]);
    }

    [Fact]
    public void AssetReference_AdditionsCatalog_AcceptsExistingFile()
    {
        // Compile-time: when an additions catalog reports the file exists,
        // the asset reference passes. The catalog is queried by category +
        // logical name (path under the category folder, extension stripped).
        using var dir = new TempDirectory();
        var spritesDir = Path.Combine(dir.Path, "sprites");
        Directory.CreateDirectory(Path.Combine(spritesDir, "item"));
        File.WriteAllBytes(Path.Combine(spritesDir, "item", "fancy-pen-icon.png"), [0]);

        var additions = new FileSystemAssetAdditionsCatalog(dir.Path);
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureAssetHolder",
                TemplateId = "x",
                Set = [new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Set,
                    FieldPath = "Icon",
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.AssetReference,
                        Asset = new CompiledAssetReference { Name = "item/fancy-pen-icon" },
                    },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(
            patches, clones: null, catalog, log, additions);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void AssetReference_AdditionsCatalog_RejectsMissingFile()
    {
        using var dir = new TempDirectory();
        var additions = new FileSystemAssetAdditionsCatalog(dir.Path);

        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureAssetHolder",
                TemplateId = "x",
                Set = [new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Set,
                    FieldPath = "Icon",
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.AssetReference,
                        Asset = new CompiledAssetReference { Name = "item/missing" },
                    },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(
            patches, clones: null, catalog, log, additions);

        Assert.Equal(1, errors);
        Assert.Contains("assets/additions/sprites/item/missing", log.Errors[0]);
    }

    [Fact]
    public void AssetReference_AdditionsCatalog_FlagsDuplicateLogicalNames()
    {
        // Same stem, different extensions in the same category folder: the
        // runtime can't disambiguate by name, so the compiler must reject.
        using var dir = new TempDirectory();
        var spritesDir = Path.Combine(dir.Path, "sprites");
        Directory.CreateDirectory(spritesDir);
        File.WriteAllBytes(Path.Combine(spritesDir, "icon.png"), [0]);
        File.WriteAllBytes(Path.Combine(spritesDir, "icon.jpg"), [0]);

        var additions = new FileSystemAssetAdditionsCatalog(dir.Path);

        Assert.NotEmpty(additions.ConflictingNames);
        Assert.Contains("sprites/icon", additions.ConflictingNames);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "jiangyu-additions-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void AssetReference_EmptyName_Errors()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureAssetHolder",
                TemplateId = "x",
                Set = [new CompiledTemplateSetOperation
                {
                    Op = CompiledTemplateOp.Set,
                    FieldPath = "Icon",
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.AssetReference,
                        Asset = new CompiledAssetReference { Name = "" },
                    },
                }],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(1, errors);
        Assert.Contains("empty", log.Errors[0]);
    }

    // Asset references fall through additions catalog → game asset index, so
    // a name in the vanilla game (e.g. asset="rmc_default_male_soldier") is
    // accepted even when the mod doesn't ship its own copy. Only the
    // "neither catalog has it" case errors.

    [Fact]
    public void AssetReference_VanillaAccepted_WhenAdditionsCatalogEmpty()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var additions = new EmptyAdditions();
        var gameAssets = new GameAssetIndex(new AssetIndex
        {
            Assets = new List<AssetEntry>
            {
                new() { Name = "vanilla_prefab", ClassName = "GameObject" },
            },
        });

        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureAssetHolder",
                TemplateId = "x",
                Set = [PrefabAssetSet("vanilla_prefab")],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log, additions, gameAssets);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void AssetReference_NeitherCatalog_Errors()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var additions = new EmptyAdditions();
        var gameAssets = new GameAssetIndex(new AssetIndex { Assets = new() });

        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureAssetHolder",
                TemplateId = "x",
                Set = [PrefabAssetSet("nobody_has_this")],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log, additions, gameAssets);

        Assert.Equal(1, errors);
        Assert.Contains("nobody_has_this", log.Errors[0]);
        Assert.Contains("no vanilla game asset", log.Errors[0]);
    }

    [Fact]
    public void AssetReference_AdditionsCatalogWins_WhenBothHaveIt()
    {
        // The mod's local additions take precedence — the validator never
        // even consults the game index when additions.Contains returns true.
        // This documents the order of resolution: project-shipped first,
        // vanilla fallback only when missing locally.
        using var catalog = Load();
        var log = new RecordingLog();
        var additions = new StubAdditions(AssetCategory.Prefabs, "shared_name");
        var gameAssets = new GameAssetIndex(new AssetIndex
        {
            Assets = new List<AssetEntry>
            {
                new() { Name = "shared_name", ClassName = "GameObject" },
            },
        });

        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureAssetHolder",
                TemplateId = "x",
                Set = [PrefabAssetSet("shared_name")],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log, additions, gameAssets);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void TaggedStringList_ResolvesDiscriminatorAndPreservesDiscriminator()
    {
        // Modder writes append "m_SerializedItems" type="FixtureTaggedAlpha" { set "AlphaText" "..." }.
        // Validator must:
        //   - substitute destinationType to TaggedPolymorphicBase
        //     (FixtureTaggedBase) for the composite dispatch
        //   - resolve "FixtureTaggedAlpha" to the CLR type
        //   - preserve the modder's discriminator on the composite for the
        //     applier to use when packing to "TYPE|{json}"
        using var catalog = Load();
        var log = new RecordingLog();
        var composite = new CompiledTemplateComposite
        {
            TypeName = "FixtureTaggedAlpha",
            Operations =
            [
                new()
                {
                    Op = CompiledTemplateOp.Set,
                    FieldPath = "AlphaText",
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.String,
                        String = "hello",
                    },
                },
            ],
        };
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureTaggedListContainer",
                TemplateId = "x",
                Set =
                [
                    new()
                    {
                        Op = CompiledTemplateOp.Append,
                        FieldPath = "m_SerializedItems",
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.Composite,
                            Composite = composite,
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
        Assert.Equal("FixtureTaggedAlpha", composite.TaggedDiscriminator);
        Assert.EndsWith("FixtureTaggedAlpha", composite.TypeName);
    }

    [Fact]
    public void TaggedString_ScalarFieldAlsoResolvesDiscriminator()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var composite = new CompiledTemplateComposite
        {
            TypeName = "FixtureTaggedBeta",
            Operations =
            [
                new()
                {
                    Op = CompiledTemplateOp.Set,
                    FieldPath = "BetaCount",
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.Int32,
                        Int32 = 7,
                    },
                },
            ],
        };
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureTaggedScalarHolder",
                TemplateId = "x",
                Set =
                [
                    new()
                    {
                        Op = CompiledTemplateOp.Set,
                        FieldPath = "m_SerAction",
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.Composite,
                            Composite = composite,
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
        Assert.Equal("FixtureTaggedBeta", composite.TaggedDiscriminator);
        Assert.EndsWith("FixtureTaggedBeta", composite.TypeName);
    }

    [Fact]
    public void TaggedString_DiscriminatorHeuristic_StripsSuffixAndUppercases()
    {
        // BaseSampleNode + ChatSampleNode/JumpSampleNode mirror the
        // BaseConversationNode + ActionConversationNode shape. Modder
        // writes the upper-cased family-suffix-stripped discriminator
        // ("CHAT" for ChatSampleNode); validator resolves through the
        // candidate set, not by class short name.
        using var catalog = Load();
        var log = new RecordingLog();
        var composite = new CompiledTemplateComposite
        {
            TypeName = "CHAT",
            Operations =
            [
                new()
                {
                    Op = CompiledTemplateOp.Set,
                    FieldPath = "Text",
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.String,
                        String = "hi",
                    },
                },
            ],
        };
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "SampleNodeContainer",
                TemplateId = "x",
                Set =
                [
                    new()
                    {
                        Op = CompiledTemplateOp.Append,
                        FieldPath = "m_SerializedNodes",
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.Composite,
                            Composite = composite,
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.Equal(0, errors);
        Assert.Empty(log.Errors);
        Assert.Equal("CHAT", composite.TaggedDiscriminator);
        Assert.EndsWith("ChatSampleNode", composite.TypeName);
    }

    [Fact]
    public void TaggedString_NestedRecursion_ResolvesAtEveryLevel()
    {
        // Mirrors VARIATION-inside-m_SerializedNodes:
        //   append "m_SerializedNodes" type="Wrapper" {
        //       append "NestedContainers" {           // inferred composite
        //           append "m_SerializedNodes" type="CHAT" { ... }
        //       }
        //   }
        // The inner type="CHAT" must resolve through the inner
        // SampleNodeContainer.m_SerializedNodes' TaggedPolymorphicBase
        // metadata, recursively.
        using var catalog = Load();
        var log = new RecordingLog();

        var innerSay = new CompiledTemplateComposite
        {
            TypeName = "CHAT",
            Operations =
            [
                new()
                {
                    Op = CompiledTemplateOp.Set,
                    FieldPath = "Text",
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.String,
                        String = "test",
                    },
                },
            ],
        };
        var innerContainer = new CompiledTemplateComposite
        {
            // Inferred (TypeName=""); validator fills SampleNodeContainer.
            TypeName = string.Empty,
            Operations =
            [
                new()
                {
                    Op = CompiledTemplateOp.Append,
                    FieldPath = "m_SerializedNodes",
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.Composite,
                        Composite = innerSay,
                    },
                },
            ],
        };
        var outerWrapper = new CompiledTemplateComposite
        {
            TypeName = "WRAPPER",
            Operations =
            [
                new()
                {
                    Op = CompiledTemplateOp.Append,
                    FieldPath = "NestedContainers",
                    Value = new CompiledTemplateValue
                    {
                        Kind = CompiledTemplateValueKind.Composite,
                        Composite = innerContainer,
                    },
                },
            ],
        };
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "SampleNodeContainer",
                TemplateId = "x",
                Set =
                [
                    new()
                    {
                        Op = CompiledTemplateOp.Append,
                        FieldPath = "m_SerializedNodes",
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.Composite,
                            Composite = outerWrapper,
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.True(errors == 0, "errors: " + string.Join("\n", log.Errors));
        Assert.Equal("WRAPPER", outerWrapper.TaggedDiscriminator);
        Assert.Equal("CHAT", innerSay.TaggedDiscriminator);
    }

    [Fact]
    public void TaggedString_UnknownDiscriminator_Errors()
    {
        using var catalog = Load();
        var log = new RecordingLog();
        var composite = new CompiledTemplateComposite
        {
            TypeName = "NotASubtype",
            Operations = [],
        };
        var patches = new[]
        {
            new CompiledTemplatePatch
            {
                TemplateType = "FixtureTaggedListContainer",
                TemplateId = "x",
                Set =
                [
                    new()
                    {
                        Op = CompiledTemplateOp.Append,
                        FieldPath = "m_SerializedItems",
                        Value = new CompiledTemplateValue
                        {
                            Kind = CompiledTemplateValueKind.Composite,
                            Composite = composite,
                        },
                    },
                ],
            },
        };

        var errors = TemplateCatalogValidator.Validate(patches, clones: null, catalog, log);

        Assert.True(errors > 0);
        Assert.NotEmpty(log.Errors);
    }

    private static CompiledTemplateSetOperation PrefabAssetSet(string assetName) => new()
    {
        Op = CompiledTemplateOp.Set,
        FieldPath = "Prefab",
        Value = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.AssetReference,
            Asset = new CompiledAssetReference { Name = assetName },
        },
    };

    private sealed class EmptyAdditions : IAssetAdditionsCatalog
    {
        public bool Contains(string category, string name) => false;
    }

    private sealed class StubAdditions : IAssetAdditionsCatalog
    {
        private readonly string _category;
        private readonly string _name;
        public StubAdditions(string category, string name) { _category = category; _name = name; }
        public bool Contains(string category, string name)
            => category == _category && name == _name;
    }
}
