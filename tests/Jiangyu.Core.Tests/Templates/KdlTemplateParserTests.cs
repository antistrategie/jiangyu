using Jiangyu.Core.Templates;
using Jiangyu.Shared.Templates;
using static Jiangyu.Core.Tests.Templates.CompiledTemplateTestHelpers;

namespace Jiangyu.Core.Tests.Templates;

public class KdlTemplateParserTests
{
    private readonly TestLogSink _log = new();

    [Fact]
    public void ParseAll_SimplePatch_ProducesCorrectModel()
    {
        var dir = SetupKdl("test.kdl", """
            patch "EntityTemplate" "player_squad.darby" {
                set "HudYOffsetScale" 5.0
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        Assert.Single(result.Patches);
        Assert.Empty(result.Clones);

        var patch = result.Patches[0];
        Assert.Equal("EntityTemplate", patch.TemplateType);
        Assert.Equal("player_squad.darby", patch.TemplateId);
        Assert.Single(patch.Set);

        var op = patch.Set[0];
        Assert.Equal(CompiledTemplateOp.Set, op.Op);
        Assert.Equal("HudYOffsetScale", op.FieldPath);
        Assert.Equal(CompiledTemplateValueKind.Single, op.Value!.Kind);
        Assert.Equal(5.0f, op.Value.Single);
    }

    [Fact]
    public void ParseAll_Clone_WithInlinePatches()
    {
        var dir = SetupKdl("clones.kdl", """
            clone "UnitLeaderTemplate" from="squad_leader.darby" id="squad_leader.darby_custom" {
                set "InitialAttributes.Vitality" 80
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        Assert.Single(result.Clones);
        Assert.Single(result.Patches);

        var clone = result.Clones[0];
        Assert.Equal("UnitLeaderTemplate", clone.TemplateType);
        Assert.Equal("squad_leader.darby", clone.SourceId);
        Assert.Equal("squad_leader.darby_custom", clone.CloneId);

        // Inline patches target the clone ID
        var patch = result.Patches[0];
        Assert.Equal("UnitLeaderTemplate", patch.TemplateType);
        Assert.Equal("squad_leader.darby_custom", patch.TemplateId);
        Assert.Single(patch.Set);
        Assert.Equal("InitialAttributes.Vitality", patch.Set[0].FieldPath);
    }

    [Fact]
    public void ParseAll_CloneWithoutBody_ProducesNoPatches()
    {
        var dir = SetupKdl("clone-only.kdl", """
            clone "UnitLeaderTemplate" from="squad_leader.darby" id="squad_leader.darby_copy"
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        Assert.Single(result.Clones);
        Assert.Empty(result.Patches);
    }

    [Fact]
    public void ParseAll_AllValueKinds()
    {
        var dir = SetupKdl("values.kdl", """
            patch "SkillTemplate" "active.aimed_shot" {
                set "FloatField" 3.14
                set "BoolField" #true
                set "IntField" 42
                set "StringField" "hello"
                set "RefField" ref="PerkTreeTemplate" "perk_tree.greifinger"
                set "EnumField" enum="MovementType" "Sprint"
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var ops = result.Patches[0].Set;
        Assert.Equal(6, ops.Count);

        // Float
        var floatValue = Assert.IsType<CompiledTemplateValue>(ops[0].Value);
        Assert.Equal(CompiledTemplateValueKind.Single, floatValue.Kind);
        Assert.Equal(3.14f, floatValue.Single);

        // Boolean
        var boolValue = Assert.IsType<CompiledTemplateValue>(ops[1].Value);
        Assert.Equal(CompiledTemplateValueKind.Boolean, boolValue.Kind);
        Assert.True(boolValue.Boolean == true);

        // Integer -> Int32
        var intValue = Assert.IsType<CompiledTemplateValue>(ops[2].Value);
        Assert.Equal(CompiledTemplateValueKind.Int32, intValue.Kind);
        Assert.Equal(42, intValue.Int32);

        // String
        var stringValue = Assert.IsType<CompiledTemplateValue>(ops[3].Value);
        Assert.Equal(CompiledTemplateValueKind.String, stringValue.Kind);
        Assert.Equal("hello", stringValue.String);

        // TemplateReference
        var referenceValue = Assert.IsType<CompiledTemplateValue>(ops[4].Value);
        Assert.Equal(CompiledTemplateValueKind.TemplateReference, referenceValue.Kind);
        var reference = Assert.IsType<CompiledTemplateReference>(referenceValue.Reference);
        Assert.Equal("PerkTreeTemplate", reference.TemplateType);
        Assert.Equal("perk_tree.greifinger", reference.TemplateId);

        // Enum
        var enumValue = Assert.IsType<CompiledTemplateValue>(ops[5].Value);
        Assert.Equal(CompiledTemplateValueKind.Enum, enumValue.Kind);
        Assert.Equal("MovementType", enumValue.EnumType);
        Assert.Equal("Sprint", enumValue.EnumValue);
    }

    [Fact]
    public void ParseAll_CollectionOps()
    {
        var dir = SetupKdl("collections.kdl", """
            patch "WeaponTemplate" "weapon.test" {
                append "SkillsGranted" ref="SkillTemplate" "active.aimed_shot"
                insert "PerkTrees" index=0 ref="PerkTreeTemplate" "perk_tree.tech"
                remove "PerkTrees" index=2
                clear "SkillsGranted"
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var ops = result.Patches[0].Set;
        Assert.Equal(4, ops.Count);

        // Append
        Assert.Equal(CompiledTemplateOp.Append, ops[0].Op);
        Assert.Equal("SkillsGranted", ops[0].FieldPath);
        Assert.Equal(CompiledTemplateValueKind.TemplateReference, ops[0].Value!.Kind);

        // InsertAt
        Assert.Equal(CompiledTemplateOp.InsertAt, ops[1].Op);
        Assert.Equal("PerkTrees", ops[1].FieldPath);
        Assert.Equal(0, ops[1].Index);
        Assert.Equal(CompiledTemplateValueKind.TemplateReference, ops[1].Value!.Kind);

        // Remove
        Assert.Equal(CompiledTemplateOp.Remove, ops[2].Op);
        Assert.Equal("PerkTrees", ops[2].FieldPath);
        Assert.Equal(2, ops[2].Index);
        Assert.Null(ops[2].Value);

        // Clear: no index, no value
        Assert.Equal(CompiledTemplateOp.Clear, ops[3].Op);
        Assert.Equal("SkillsGranted", ops[3].FieldPath);
        Assert.Null(ops[3].Index);
        Assert.Null(ops[3].Value);
    }

    [Fact]
    public void ParseAll_ClearRejectsIndex()
    {
        var dir = SetupKdl("clear-with-index.kdl", """
            patch "WeaponTemplate" "weapon.test" {
                clear "SkillsGranted" index=0
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.NotEqual(0, result.ErrorCount);
        Assert.Contains(_log.Errors, e => e.Contains("'clear' does not take an index"));
    }

    [Fact]
    public void ParseAll_ClearRejectsValue()
    {
        var dir = SetupKdl("clear-with-value.kdl", """
            patch "WeaponTemplate" "weapon.test" {
                clear "SkillsGranted" "extra"
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.NotEqual(0, result.ErrorCount);
        Assert.Contains(_log.Errors, e => e.Contains("'clear' takes no value"));
    }

    [Fact]
    public void ParseAll_Composite()
    {
        var dir = SetupKdl("composite.kdl", """
            patch "PerkTreeTemplate" "perk_tree.darby" {
                append "Perks" composite="Perk" {
                    set "Skill" ref="PerkTemplate" "perk.athletic"
                    set "Tier" 3
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var op = result.Patches[0].Set[0];
        Assert.Equal(CompiledTemplateOp.Append, op.Op);
        Assert.Equal(CompiledTemplateValueKind.Composite, op.Value!.Kind);

        var composite = op.Value.Composite!;
        Assert.Equal("Perk", composite.TypeName);
        Assert.Equal(2, composite.Operations.Count);
        Assert.Equal(CompiledTemplateValueKind.TemplateReference, composite.ValueAt("Skill").Kind);
        Assert.Equal(CompiledTemplateValueKind.Int32, composite.ValueAt("Tier").Kind);
        Assert.Equal(3, composite.ValueAt("Tier").Int32);
    }

    [Fact]
    public void ParseAll_DuplicateInFile_IsError()
    {
        var dir = SetupKdl("dup.kdl", """
            patch "EntityTemplate" "player_squad.darby" {
                set "HudYOffsetScale" 5.0
            }
            patch "EntityTemplate" "player_squad.darby" {
                set "HudYOffsetScale" 10.0
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.True(result.ErrorCount > 0);
        Assert.Contains("duplicate", _log.Errors.First(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseAll_DuplicateAcrossFiles_IsError()
    {
        var dir = SetupKdl(
            ("a.kdl", """
                patch "EntityTemplate" "player_squad.darby" {
                    set "HudYOffsetScale" 5.0
                }
                """),
            ("b.kdl", """
                patch "EntityTemplate" "player_squad.darby" {
                    set "HudYOffsetScale" 10.0
                }
                """));

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.True(result.ErrorCount > 0);
        Assert.Contains("duplicate", _log.Errors.First(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseAll_RecursiveDiscovery()
    {
        var dir = SetupKdl(
            ("units/darby.kdl", """
                patch "EntityTemplate" "player_squad.darby" {
                    set "HudYOffsetScale" 5.0
                }
                """),
            ("weapons/carbine.kdl", """
                patch "WeaponTemplate" "weapon.generic_carbine" {
                    set "Damage" 25
                }
                """));

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(2, result.Patches.Count);
    }

    [Fact]
    public void ParseAll_UnknownTopLevelNode_IsError()
    {
        var dir = SetupKdl("bad.kdl", """
            modify "EntityTemplate" "player_squad.darby" {
                set "HudYOffsetScale" 5.0
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.True(result.ErrorCount > 0);
        Assert.Contains("unknown top-level node", _log.Errors.First(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseAll_MissingArguments_IsError()
    {
        var dir = SetupKdl("bad.kdl", """
            patch "EntityTemplate" {
                set "HudYOffsetScale" 5.0
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.True(result.ErrorCount > 0);
    }

    [Fact]
    public void ParseAll_EmptyPatchBody_IsOk()
    {
        var dir = SetupKdl("empty.kdl", """
            patch "EntityTemplate" "player_squad.darby"
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        Assert.Single(result.Patches);
        Assert.Empty(result.Patches[0].Set);
    }

    [Fact]
    public void ParseAll_NegativeInteger_EmitsInt32()
    {
        var dir = SetupKdl("neg.kdl", """
            patch "EntityTemplate" "player_squad.darby" {
                set "SomeField" -5
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var op = result.Patches[0].Set[0];
        Assert.Equal(CompiledTemplateValueKind.Int32, op.Value!.Kind);
        Assert.Equal(-5, op.Value.Int32);
    }

    [Fact]
    public void ParseAll_DottedFieldPath_Preserved()
    {
        var dir = SetupKdl("dotted.kdl", """
            patch "SkillTemplate" "active.aimed_shot" {
                set "ProjectileData.TimeScale" 99.0
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal("ProjectileData.TimeScale", result.Patches[0].Set[0].FieldPath);
    }

    // --- Descent block (child-block) grammar ---

    [Fact]
    public void DescentBlock_FlattensInnerSetIntoIndexedPath()
    {
        var dir = SetupKdl("desc.kdl", """
            patch "PerkTemplate" "perk.unique_darby_high_value_targets" {
                set "EventHandlers" index=0 type="AddSkill" {
                    set "ShowHUDText" #true
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        Assert.Single(result.Patches);
        var ops = result.Patches[0].Set;
        Assert.Single(ops);

        var op = ops[0];
        Assert.Equal(CompiledTemplateOp.Set, op.Op);
        Assert.Equal("ShowHUDText", op.FieldPath);
        Assert.Equal(CompiledTemplateValueKind.Boolean, op.Value!.Kind);
        Assert.True(op.Value.Boolean);
        Assert.NotNull(op.Descent);
        Assert.Single(op.Descent);
        Assert.Equal("EventHandlers", op.Descent[0].Field);
        Assert.Equal(0, op.Descent[0].Index);
        Assert.Equal("AddSkill", op.Descent[0].Subtype);
    }

    [Fact]
    public void DescentBlock_MultipleInnerSetsAllShareDescentPrefix()
    {
        var dir = SetupKdl("desc.kdl", """
            patch "PerkTemplate" "perk.unique_darby_high_value_targets" {
                set "EventHandlers" index=0 type="AddSkill" {
                    set "ShowHUDText" #true
                    set "OnlyApplyOnHit" #true
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var ops = result.Patches[0].Set;
        Assert.Equal(2, ops.Count);
        Assert.Equal("ShowHUDText", ops[0].FieldPath);
        Assert.Equal("OnlyApplyOnHit", ops[1].FieldPath);
        Assert.NotNull(ops[0].Descent);
        Assert.Single(ops[0].Descent!);
        Assert.Equal("EventHandlers", ops[0].Descent![0].Field);
        Assert.Equal(0, ops[0].Descent![0].Index);
        Assert.Equal("AddSkill", ops[0].Descent![0].Subtype);
        Assert.NotNull(ops[1].Descent);
        Assert.Single(ops[1].Descent!);
        Assert.Equal("EventHandlers", ops[1].Descent![0].Field);
        Assert.Equal(0, ops[1].Descent![0].Index);
        Assert.Equal("AddSkill", ops[1].Descent![0].Subtype);
    }

    [Fact]
    public void DescentBlock_NestedDescent_ShiftsInnerHintWhenOuterUnhinted()
    {
        // Outer descent without type=, inner descent with type=. The inner
        // hint stays on the inner step; the outer step has a null Subtype.
        var dir = SetupKdl("desc.kdl", """
            patch "PerkTemplate" "perk.x" {
                set "Outer" index=0 {
                    set "Inner" index=2 type="Y" {
                        set "Leaf" 5
                    }
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var op = result.Patches[0].Set[0];
        Assert.Equal("Leaf", op.FieldPath);
        Assert.NotNull(op.Descent);
        Assert.Equal(2, op.Descent.Count);
        Assert.Equal("Outer", op.Descent[0].Field);
        Assert.Equal(0, op.Descent[0].Index);
        Assert.Null(op.Descent[0].Subtype);
        Assert.Equal("Inner", op.Descent[1].Field);
        Assert.Equal(2, op.Descent[1].Index);
        Assert.Equal("Y", op.Descent[1].Subtype);
    }

    [Fact]
    public void DescentBlock_SiblingDescentsWithSamePrefix_StayDistinct()
    {
        // Two consecutive descents into the same parent index should be
        // independent ops in the flattened model — the serialiser later
        // collapses them into one outer block at emit time, but the parser
        // shouldn't merge them at parse time. Each inner directive ends up
        // as its own op with the same prefix.
        var dir = SetupKdl("desc.kdl", """
            patch "PerkTemplate" "perk.x" {
                set "EventHandlers" index=0 type="AddSkill" {
                    set "ShowHUDText" #true
                }
                set "EventHandlers" index=0 type="AddSkill" {
                    set "OnlyApplyOnHit" #true
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var ops = result.Patches[0].Set;
        Assert.Equal(2, ops.Count);
        Assert.Equal("ShowHUDText", ops[0].FieldPath);
        Assert.Equal("OnlyApplyOnHit", ops[1].FieldPath);
        Assert.NotNull(ops[0].Descent);
        Assert.Single(ops[0].Descent!);
        Assert.Equal("EventHandlers", ops[0].Descent![0].Field);
        Assert.Equal(0, ops[0].Descent![0].Index);
        Assert.Equal("AddSkill", ops[0].Descent![0].Subtype);
        Assert.NotNull(ops[1].Descent);
        Assert.Single(ops[1].Descent!);
        Assert.Equal("EventHandlers", ops[1].Descent![0].Field);
        Assert.Equal(0, ops[1].Descent![0].Index);
        Assert.Equal("AddSkill", ops[1].Descent![0].Subtype);
    }

    [Fact]
    public void DescentBlock_NestedDescentShiftsInnerHints()
    {
        var dir = SetupKdl("desc.kdl", """
            patch "PerkTemplate" "perk.x" {
                set "Outer" index=0 type="X" {
                    set "Inner" index=2 type="Y" {
                        set "Leaf" 5
                    }
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var ops = result.Patches[0].Set;
        Assert.Single(ops);
        Assert.Equal("Leaf", ops[0].FieldPath);
        Assert.NotNull(ops[0].Descent);
        Assert.Equal(2, ops[0].Descent!.Count);
        Assert.Equal("Outer", ops[0].Descent![0].Field);
        Assert.Equal(0, ops[0].Descent![0].Index);
        Assert.Equal("X", ops[0].Descent![0].Subtype);
        Assert.Equal("Inner", ops[0].Descent![1].Field);
        Assert.Equal(2, ops[0].Descent![1].Index);
        Assert.Equal("Y", ops[0].Descent![1].Subtype);
    }

    [Fact]
    public void DescentBlock_TerminalElementWriteInsidePreservesIndex()
    {
        // Inner set has its own index= for a NamedArray-style element write;
        // outer descent prefixes the path but the inner Index field is
        // preserved.
        var dir = SetupKdl("desc.kdl", """
            patch "PerkTemplate" "perk.x" {
                set "EventHandlers" index=0 type="AddSkill" {
                    set "TargetRequiresOneOfTheseTags" index=2 ref="TagTemplate" "infantry"
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var op = result.Patches[0].Set[0];
        Assert.Equal("TargetRequiresOneOfTheseTags", op.FieldPath);
        Assert.Equal(2, op.Index);
        Assert.NotNull(op.Descent);
        Assert.Single(op.Descent!);
        Assert.Equal("EventHandlers", op.Descent![0].Field);
        Assert.Equal(0, op.Descent![0].Index);
        Assert.Equal("AddSkill", op.Descent![0].Subtype);
        Assert.Equal(CompiledTemplateValueKind.TemplateReference, op.Value!.Kind);
    }

    [Fact]
    public void DescentBlock_TypeHintOptional_WhenElementTypeUnambiguous()
    {
        // Validator decides whether type= is required; the parser accepts
        // descent without type= and produces a step whose Subtype is null.
        var dir = SetupKdl("desc.kdl", """
            patch "EntityTemplate" "player_squad.darby" {
                set "Properties" index=0 {
                    set "Accuracy" 80.0
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var op = result.Patches[0].Set[0];
        Assert.Equal("Accuracy", op.FieldPath);
        Assert.NotNull(op.Descent);
        Assert.Single(op.Descent);
        Assert.Equal("Properties", op.Descent[0].Field);
        Assert.Equal(0, op.Descent[0].Index);
        Assert.Null(op.Descent[0].Subtype);
    }

    [Fact]
    public void HardCut_BracketIndexerInFieldPath_IsRejected()
    {
        var dir = SetupKdl("bad.kdl", """
            patch "PerkTemplate" "perk.x" {
                set "EventHandlers[0].ShowHUDText" #true
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Empty(result.Patches);
        Assert.Contains(_log.Errors, e => e.Contains("bracket indexer", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(_log.Errors, e => e.Contains("child block", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HardCut_BracketInNonSetOp_IsRejected()
    {
        var dir = SetupKdl("bad.kdl", """
            patch "PerkTemplate" "perk.x" {
                append "EventHandlers[0].Tags" ref="TagTemplate" "infantry"
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(_log.Errors, e => e.Contains("bracket indexer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DescentBlock_RequiresIndexProperty()
    {
        var dir = SetupKdl("bad.kdl", """
            patch "PerkTemplate" "perk.x" {
                set "EventHandlers" type="AddSkill" {
                    set "ShowHUDText" #true
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(_log.Errors, e => e.Contains("index=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DescentBlock_RejectsValueOnOuterSet()
    {
        var dir = SetupKdl("bad.kdl", """
            patch "PerkTemplate" "perk.x" {
                set "EventHandlers" index=0 type="AddSkill" 5 {
                    set "ShowHUDText" #true
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(_log.Errors, e => e.Contains("must not carry a value", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DescentBlock_RejectsEmptyChildBlock()
    {
        // KDL's HasChildren returns false for `{ }` so the empty-block case
        // currently falls through to the value-required branch. Either
        // error is correct (modder wrote something malformed); we just
        // assert the parse fails. If the parser ever distinguishes empty
        // child blocks at the lexer level, tighten the message check.
        var dir = SetupKdl("bad.kdl", """
            patch "PerkTemplate" "perk.x" {
                set "EventHandlers" index=0 type="AddSkill" {
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Empty(result.Patches);
    }

    [Fact]
    public void DescentBlock_RejectsNonSetInnerOps()
    {
        var dir = SetupKdl("bad.kdl", """
            patch "PerkTemplate" "perk.x" {
                set "EventHandlers" index=0 type="AddSkill" {
                    append "TargetTags" ref="TagTemplate" "infantry"
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(_log.Errors, e => e.Contains("only 'set'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TypeHint_WithoutChildBlock_IsRejected()
    {
        var dir = SetupKdl("bad.kdl", """
            patch "PerkTemplate" "perk.x" {
                set "Field" index=0 type="X" 5
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(_log.Errors, e => e.Contains("type=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TypeHint_OnAppend_IsRejected()
    {
        var dir = SetupKdl("bad.kdl", """
            patch "PerkTemplate" "perk.x" {
                append "Items" type="X" ref="ItemTemplate" "foo"
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(_log.Errors, e => e.Contains("type=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DescentBlock_RejectsRefOrEnumOnOuterSet()
    {
        // ref= with a value argument trips the no-value check before the
        // no-ref check; either error is correct — both flag the same modder
        // mistake. We keep this test loose because the precise order of
        // failure within the descent block validation is not load-bearing.
        var dir = SetupKdl("bad.kdl", """
            patch "PerkTemplate" "perk.x" {
                set "EventHandlers" index=0 type="AddSkill" ref="X" "y" {
                    set "ShowHUDText" #true
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Empty(result.Patches);
    }

    [Fact]
    public void DescentBlock_RejectsCompositeOnOuterSet()
    {
        // composite= takes the value-construction path, so type= ends up
        // ignored — but we reject type= outside descent to keep modder
        // intent unambiguous. The error tells them to drop one or the other.
        var dir = SetupKdl("bad.kdl", """
            patch "PerkTemplate" "perk.x" {
                set "EventHandlers" index=0 type="AddSkill" composite="X" {
                    set "ShowHUDText" #true
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(_log.Errors, e => e.Contains("type=", StringComparison.OrdinalIgnoreCase));
    }

    // --- Handler construction (slice 4b) ---

    [Fact]
    public void HandlerConstruction_AppendCarriesSubtypeAndFields()
    {
        var dir = SetupKdl("h.kdl", """
            patch "SkillTemplate" "active.foo" {
                append "EventHandlers" handler="AddSkill" {
                    set "Event" enum="AddEvent" "OnHit"
                    set "OnlyApplyOnHit" #true
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var op = result.Patches[0].Set[0];
        Assert.Equal(CompiledTemplateOp.Append, op.Op);
        Assert.Equal("EventHandlers", op.FieldPath);
        Assert.Equal(CompiledTemplateValueKind.HandlerConstruction, op.Value!.Kind);
        Assert.Equal("AddSkill", op.Value.HandlerConstruction!.TypeName);
        Assert.Equal(2, op.Value.HandlerConstruction.Operations.Count);
        Assert.Equal(CompiledTemplateValueKind.Enum, op.Value.HandlerConstruction.ValueAt("Event").Kind);
        Assert.Equal(CompiledTemplateValueKind.Boolean, op.Value.HandlerConstruction.ValueAt("OnlyApplyOnHit").Kind);
    }

    [Fact]
    public void HandlerConstruction_InsertCarriesIndex()
    {
        var dir = SetupKdl("h.kdl", """
            patch "SkillTemplate" "active.foo" {
                insert "EventHandlers" index=2 handler="ChangeProperty" {
                    set "Amount" 5
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var op = result.Patches[0].Set[0];
        Assert.Equal(CompiledTemplateOp.InsertAt, op.Op);
        Assert.Equal(2, op.Index);
        Assert.Equal("ChangeProperty", op.Value!.HandlerConstruction!.TypeName);
    }

    [Fact]
    public void HandlerConstruction_SetWithIndexReplacesElement()
    {
        // set with index= and handler= means "replace element N with a fresh
        // construction" — different from descent (which edits in place).
        var dir = SetupKdl("h.kdl", """
            patch "SkillTemplate" "active.foo" {
                set "EventHandlers" index=1 handler="Cooldown" {
                    set "RoundsToCoolDown" 3
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var op = result.Patches[0].Set[0];
        Assert.Equal(CompiledTemplateOp.Set, op.Op);
        Assert.Equal(1, op.Index);
        Assert.Equal("Cooldown", op.Value!.HandlerConstruction!.TypeName);
    }

    [Fact]
    public void HandlerConstruction_RejectsRefAndCompositeMixing()
    {
        var dir = SetupKdl("h.kdl", """
            patch "SkillTemplate" "active.foo" {
                append "EventHandlers" handler="AddSkill" composite="X" {
                    set "Event" enum="AddEvent" "OnHit"
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(_log.Errors, e => e.Contains("exclusive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HandlerConstruction_RejectsTypeMixing()
    {
        // type= is for descent into existing elements; mixing with handler=
        // (which constructs new) is contradictory — surface as a typo.
        var dir = SetupKdl("h.kdl", """
            patch "SkillTemplate" "active.foo" {
                append "EventHandlers" handler="AddSkill" type="X" {
                    set "Event" enum="AddEvent" "OnHit"
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
    }

    [Fact]
    public void HandlerConstruction_RejectsEmptyChildBlock()
    {
        var dir = SetupKdl("h.kdl", """
            patch "SkillTemplate" "active.foo" {
                append "EventHandlers" handler="AddSkill" {
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
    }

    [Fact]
    public void HandlerConstruction_AllowsAllInnerOps()
    {
        // Inner directives mirror outer directive semantics — set / append /
        // insert / remove / clear are all valid against the constructed
        // instance's fields. Modders can author "construct a handler with
        // these initial Properties" inline rather than splitting into a
        // separate descent patch on the resulting list element.
        var dir = SetupKdl("h.kdl", """
            patch "SkillTemplate" "active.foo" {
                append "EventHandlers" handler="AddSkill" {
                    append "TargetTags" ref="TagTemplate" "infantry"
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
    }

    [Fact]
    public void HandlerConstruction_RejectsBracketIndexerInInnerFieldName()
    {
        var dir = SetupKdl("h.kdl", """
            patch "SkillTemplate" "active.foo" {
                append "EventHandlers" handler="AddSkill" {
                    set "TargetTags[0]" ref="TagTemplate" "infantry"
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
    }

    [Fact]
    public void HandlerConstruction_AllowsIndexOnInnerSet()
    {
        // Element-set on a collection sub-field of the constructed instance
        // (`set "TargetTags" index=0 ...`) is the same shape as outer-level
        // element-set; the inner parser delegates to TryParseOperation and
        // accepts it like any other authored op.
        var dir = SetupKdl("h.kdl", """
            patch "SkillTemplate" "active.foo" {
                append "EventHandlers" handler="AddSkill" {
                    set "TargetTags" index=0 ref="TagTemplate" "infantry"
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
    }

    [Fact]
    public void HandlerConstruction_RejectsValueOnOuterSet()
    {
        var dir = SetupKdl("h.kdl", """
            patch "SkillTemplate" "active.foo" {
                append "EventHandlers" handler="AddSkill" 5 {
                    set "Event" enum="AddEvent" "OnHit"
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
    }

    [Fact]
    public void HandlerConstruction_AppendStillRequiresChildBlock()
    {
        // Without children, the construction would be all-defaults, which is
        // almost never what the modder meant. Surface it loudly.
        var dir = SetupKdl("h.kdl", """
            patch "SkillTemplate" "active.foo" {
                append "EventHandlers" handler="AddSkill"
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
    }

    [Fact]
    public void AssetReference_BareName_ProducesAssetValue()
    {
        var dir = SetupKdl("a.kdl", """
            patch "ItemDefinition" "fancy-pen" {
                set "Icon" asset="fancy-pen-icon"
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var op = result.Patches[0].Set[0];
        Assert.Equal("Icon", op.FieldPath);
        Assert.Equal(CompiledTemplateValueKind.AssetReference, op.Value!.Kind);
        var asset = Assert.IsType<CompiledAssetReference>(op.Value.Asset);
        Assert.Equal("fancy-pen-icon", asset.Name);
    }

    [Fact]
    public void AssetReference_SlashedName_IsPreservedVerbatim()
    {
        // The recursive folder layout under assets/additions/<category>/ maps
        // file path to logical name with `/` separators kept intact, so the
        // parser must not collapse or normalise them away.
        var dir = SetupKdl("a.kdl", """
            patch "ItemDefinition" "fancy-pen" {
                set "Icon" asset="item/fancy/pen-icon"
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var op = result.Patches[0].Set[0];
        Assert.Equal("item/fancy/pen-icon", op.Value!.Asset!.Name);
    }

    [Fact]
    public void AssetReference_InsideClone_ReachesCloneIdPatch()
    {
        // Clones produce an inline patch targeting the clone ID; asset
        // references inside the clone body must compile through the same
        // path so a modder can re-skin a cloned item with a new sprite.
        var dir = SetupKdl("a.kdl", """
            clone "ItemDefinition" from="pen" id="fancy-pen" {
                set "Icon" asset="fancy-pen-icon"
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        Assert.Single(result.Clones);
        Assert.Single(result.Patches);
        var op = result.Patches[0].Set[0];
        Assert.Equal("fancy-pen", result.Patches[0].TemplateId);
        Assert.Equal("Icon", op.FieldPath);
        Assert.Equal(CompiledTemplateValueKind.AssetReference, op.Value!.Kind);
        Assert.Equal("fancy-pen-icon", op.Value.Asset!.Name);
    }

    [Fact]
    public void AssetReference_EmptyName_IsRejected()
    {
        var dir = SetupKdl("a.kdl", """
            patch "ItemDefinition" "fancy-pen" {
                set "Icon" asset=""
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(_log.Errors, e => e.Contains("asset=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AssetReference_PositionalValueAfterAsset_IsRejected()
    {
        // The asset name lives in the property value; a trailing positional
        // is almost certainly a modder confusing the asset shape with ref=.
        var dir = SetupKdl("a.kdl", """
            patch "ItemDefinition" "fancy-pen" {
                set "Icon" asset="sprite" "fancy-pen-icon"
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(_log.Errors, e => e.Contains("asset=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AssetReference_ChildBlock_IsRejected()
    {
        // An asset reference is a leaf value; a child block is meaningless.
        // The descent-block guard fires first because the outer `set` has
        // children but no index=, which is also a correct rejection.
        var dir = SetupKdl("a.kdl", """
            patch "ItemDefinition" "fancy-pen" {
                set "Icon" asset="fancy-pen-icon" {
                    set "Anything" 1
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.NotEqual(0, result.ErrorCount);
        Assert.Empty(result.Patches);
    }

    [Fact]
    public void AssetReference_TypeProperty_IsRejected()
    {
        // The Unity type comes from the destination field's declared type;
        // letting the modder write type= here would invite drift between the
        // assertion and the field, so we reject rather than coerce.
        var dir = SetupKdl("a.kdl", """
            patch "ItemDefinition" "fancy-pen" {
                set "Icon" asset="fancy-pen-icon" type="Sprite"
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.NotEqual(0, result.ErrorCount);
    }

    [Fact]
    public void AssetReference_DescentBlockWithAsset_IsRejected()
    {
        // asset= belongs on the inner set that produces the value, not on
        // the descent navigation marker; same rule as ref=/enum=/composite=.
        var dir = SetupKdl("a.kdl", """
            patch "ItemDefinition" "fancy-pen" {
                set "Decals" index=0 asset="decal" {
                    set "Tint" 1.0
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(_log.Errors, e => e.Contains("asset=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AssetReference_BackslashSeparator_IsRejected()
    {
        // Modders authoring on Windows must still write forward-slashes in
        // KDL: the asset name is portable text, and the filesystem walk
        // at compile time normalises native separators when computing the
        // logical name. Backslash in the authored form is unambiguously a
        // mistake, so surface it instead of silently translating.
        var dir = SetupKdl("a.kdl", """
            patch "ItemDefinition" "fancy-pen" {
                set "Icon" asset="lrm5\\icon"
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(_log.Errors, e => e.Contains("'/'", StringComparison.Ordinal));
    }

    [Fact]
    public void AssetReference_ConflictsWithHandler_IsRejected()
    {
        var dir = SetupKdl("a.kdl", """
            patch "SkillTemplate" "active.foo" {
                append "EventHandlers" handler="AddSkill" asset="something" {
                    set "Event" enum="AddEvent" "OnHit"
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(_log.Errors, e => e.Contains("asset=", StringComparison.OrdinalIgnoreCase));
    }

    // --- Helpers ---

    private static string SetupKdl(string fileName, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "jiangyu-test-kdl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var subDir = Path.GetDirectoryName(Path.Combine(dir, fileName));
        if (subDir != null)
            Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
        return dir;
    }

    private static string SetupKdl(params (string fileName, string content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "jiangyu-test-kdl-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var (fileName, content) in files)
        {
            var fullPath = Path.Combine(dir, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        return dir;
    }

    private sealed class TestLogSink : Jiangyu.Core.Abstractions.ILogSink
    {
        public List<string> Errors { get; } = [];
        public List<string> Warnings { get; } = [];

        public void Info(string message) { }
        public void Warning(string message) => Warnings.Add(message);
        public void Error(string message) => Errors.Add(message);
    }
}
