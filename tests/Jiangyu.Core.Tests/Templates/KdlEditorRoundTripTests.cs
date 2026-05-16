using System.Globalization;
using Jiangyu.Core.Templates;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Tests.Templates;

public class KdlEditorRoundTripTests
{
    [Fact]
    public void SimplePatch_RoundTrips()
    {
        const string kdl = """
            patch "EntityTemplate" "player_squad.darby" {
                set "HudYOffsetScale" 5.0
                set "IsActive" #true
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Single(doc.Nodes);

        var node = doc.Nodes[0];
        Assert.Equal(KdlEditorNodeKind.Patch, node.Kind);
        Assert.Equal("EntityTemplate", node.TemplateType);
        Assert.Equal("player_squad.darby", node.TemplateId);
        Assert.Equal(2, node.Directives.Count);

        var text = KdlTemplateSerialiser.Serialise(doc);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Empty(doc2.Errors);
        Assert.Single(doc2.Nodes);
        Assert.Equal(2, doc2.Nodes[0].Directives.Count);
    }

    [Fact]
    public void RefValue_RoundTrips()
    {
        const string kdl = """
            patch "UnitLeaderTemplate" "squad_leader.darby" {
                append "PerkTrees" ref="PerkTreeTemplate" "perk_tree.greifinger"
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        var d = doc.Nodes[0].Directives[0];
        Assert.Equal(KdlEditorOp.Append, d.Op);
        Assert.Equal(KdlEditorValueKind.TemplateReference, d.Value!.Kind);
        Assert.Equal("PerkTreeTemplate", d.Value.ReferenceType);
        Assert.Equal("perk_tree.greifinger", d.Value.ReferenceId);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("ref=\"PerkTreeTemplate\"", text);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Empty(doc2.Errors);
        Assert.Equal("perk_tree.greifinger", doc2.Nodes[0].Directives[0].Value!.ReferenceId);
    }

    [Fact]
    public void AssetValue_RoundTrips()
    {
        // The asset reference shape is the foundation for the prefab-cloning
        // template-redirection step, so the editor model must preserve it
        // intact. KDL: `asset="lrm5/icon"` parses into AssetReference value
        // with the modder-facing slashed name; serialise back emits the
        // same form.
        const string kdl = """
            patch "ItemDefinition" "fancy-pen" {
                set "Icon" asset="lrm5/icon"
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        var d = doc.Nodes[0].Directives[0];
        Assert.Equal(KdlEditorOp.Set, d.Op);
        Assert.Equal(KdlEditorValueKind.AssetReference, d.Value!.Kind);
        Assert.Equal("lrm5/icon", d.Value.AssetName);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("asset=\"lrm5/icon\"", text);

        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Empty(doc2.Errors);
        Assert.Equal("lrm5/icon", doc2.Nodes[0].Directives[0].Value!.AssetName);
    }

    [Fact]
    public void EnumValue_RoundTrips()
    {
        const string kdl = """
            patch "EntityTemplate" "test" {
                set "Faction" enum="FactionType" "Rebel"
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        var d = doc.Nodes[0].Directives[0];
        Assert.Equal(KdlEditorValueKind.Enum, d.Value!.Kind);
        Assert.Equal("FactionType", d.Value.EnumType);
        Assert.Equal("Rebel", d.Value.EnumValue);

        var text = KdlTemplateSerialiser.Serialise(doc);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Equal("Rebel", doc2.Nodes[0].Directives[0].Value!.EnumValue);
    }

    [Fact]
    public void InsertWithIndex_RoundTrips()
    {
        const string kdl = """
            patch "UnitLeaderTemplate" "squad_leader.darby" {
                insert "PerkTrees" index=0 ref="PerkTreeTemplate" "perk_tree.tech"
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        var d = doc.Nodes[0].Directives[0];
        Assert.Equal(KdlEditorOp.Insert, d.Op);
        Assert.Equal(0, d.Index);
        Assert.Equal(KdlEditorValueKind.TemplateReference, d.Value!.Kind);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("index=0", text);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Equal(0, doc2.Nodes[0].Directives[0].Index);
    }

    [Fact]
    public void RemoveIndexed_RoundTrips()
    {
        const string kdl = """
            patch "UnitLeaderTemplate" "squad_leader.darby" {
                remove "PerkTrees" index=0
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        var d = doc.Nodes[0].Directives[0];
        Assert.Equal(KdlEditorOp.Remove, d.Op);
        Assert.Equal("PerkTrees", d.FieldPath);
        Assert.Equal(0, d.Index);
        Assert.Null(d.Value);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("remove \"PerkTrees\" index=0", text);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Equal("PerkTrees", doc2.Nodes[0].Directives[0].FieldPath);
        Assert.Equal(0, doc2.Nodes[0].Directives[0].Index);
    }

    [Fact]
    public void RemoveByValue_RoundTrips()
    {
        // HashSet<T> Remove: by-value, no index. Parser accepts the
        // value form; serialiser emits it back the same way.
        const string kdl = """
            patch "WeaponTemplate" "weapon.test" {
                remove "ItemSlots" enum="ItemSlot" "Pistol"
                remove "SkillsRemoved" ref="SkillTemplate" "active.aimed_shot"
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);

        var slotRemove = doc.Nodes[0].Directives[0];
        Assert.Equal(KdlEditorOp.Remove, slotRemove.Op);
        Assert.Equal("ItemSlots", slotRemove.FieldPath);
        Assert.Null(slotRemove.Index);
        Assert.NotNull(slotRemove.Value);
        Assert.Equal(KdlEditorValueKind.Enum, slotRemove.Value!.Kind);
        Assert.Equal("Pistol", slotRemove.Value.EnumValue);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("remove \"ItemSlots\" enum=\"ItemSlot\" \"Pistol\"", text);
        Assert.Contains(
            "remove \"SkillsRemoved\" ref=\"SkillTemplate\" \"active.aimed_shot\"",
            text);

        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Empty(doc2.Errors);
        Assert.Equal(slotRemove.Value.EnumValue, doc2.Nodes[0].Directives[0].Value!.EnumValue);
    }

    [Fact]
    public void CompositeValue_RoundTrips()
    {
        const string kdl = """
            patch "PerkTreeTemplate" "perk_tree.darby" {
                append "Perks" composite="Perk" {
                    set "Skill" ref="PerkTemplate" "perk.athletic"
                    set "Tier" 3
                }
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        var d = doc.Nodes[0].Directives[0];
        Assert.Equal(KdlEditorOp.Append, d.Op);
        Assert.Equal(KdlEditorValueKind.Composite, d.Value!.Kind);
        Assert.Equal("Perk", d.Value.CompositeType);
        Assert.NotNull(d.Value.CompositeDirectives);
        Assert.Equal(2, d.Value.CompositeDirectives.Count);
        var skillDir = d.Value.CompositeDirectives.First(o => o.FieldPath == "Skill");
        var tierDir = d.Value.CompositeDirectives.First(o => o.FieldPath == "Tier");
        Assert.Equal(KdlEditorValueKind.TemplateReference, skillDir.Value!.Kind);
        Assert.Equal(KdlEditorValueKind.Int32, tierDir.Value!.Kind);
        Assert.Equal(3, tierDir.Value.Int32);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("composite=\"Perk\"", text);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Equal("Perk", doc2.Nodes[0].Directives[0].Value!.CompositeType);
        Assert.Equal(2, doc2.Nodes[0].Directives[0].Value!.CompositeDirectives!.Count);
    }

    [Fact]
    public void CloneWithInlineDirectives_RoundTrips()
    {
        const string kdl = """
            clone "UnitLeaderTemplate" from="leader.alice" id="leader.alice_clone" {
                set "DisplayName" "Alice Mk.II"
                set "InitialAttributes.Agility" 100
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Single(doc.Nodes);

        var node = doc.Nodes[0];
        Assert.Equal(KdlEditorNodeKind.Clone, node.Kind);
        Assert.Equal("UnitLeaderTemplate", node.TemplateType);
        Assert.Equal("leader.alice", node.SourceId);
        Assert.Equal("leader.alice_clone", node.CloneId);
        Assert.Equal(2, node.Directives.Count);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("from=\"leader.alice\"", text);
        Assert.Contains("id=\"leader.alice_clone\"", text);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Equal(KdlEditorNodeKind.Clone, doc2.Nodes[0].Kind);
        Assert.Equal(2, doc2.Nodes[0].Directives.Count);
    }

    [Fact]
    public void MultipleNodes_PreservesOrder()
    {
        const string kdl = """
            patch "EntityTemplate" "a" {
                set "X" 1
            }

            clone "UnitLeaderTemplate" from="b" id="c" {
                set "Y" 2
            }

            patch "EntityTemplate" "d" {
                set "Z" 3
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Equal(3, doc.Nodes.Count);
        Assert.Equal(KdlEditorNodeKind.Patch, doc.Nodes[0].Kind);
        Assert.Equal(KdlEditorNodeKind.Clone, doc.Nodes[1].Kind);
        Assert.Equal(KdlEditorNodeKind.Patch, doc.Nodes[2].Kind);
        Assert.Equal("a", doc.Nodes[0].TemplateId);
        Assert.Equal("d", doc.Nodes[2].TemplateId);
    }

    [Fact]
    public void ParseErrors_ReportedNotCrash()
    {
        const string kdl = "this is not valid KDL {{{}";

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.NotEmpty(doc.Errors);
    }

    [Fact]
    public void UnknownTopLevelNode_ReportsError()
    {
        const string kdl = """
            foobar "something" {
                set "x" 1
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.NotEmpty(doc.Errors);
        Assert.Contains(doc.Errors, e => e.Message.Contains("foobar"));
    }

    [Fact]
    public void BooleanValues_RoundTrip()
    {
        const string kdl = """
            patch "EntityTemplate" "test" {
                set "Active" #true
                set "Hidden" #false
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Equal(true, doc.Nodes[0].Directives[0].Value!.Boolean);
        Assert.Equal(false, doc.Nodes[0].Directives[1].Value!.Boolean);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("#true", text);
        Assert.Contains("#false", text);
    }

    [Fact]
    public void StringValue_RoundTrips()
    {
        const string kdl = """
            patch "EntityTemplate" "test" {
                set "Name" "Hello World"
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Equal(KdlEditorValueKind.String, doc.Nodes[0].Directives[0].Value!.Kind);
        Assert.Equal("Hello World", doc.Nodes[0].Directives[0].Value!.String);

        var text = KdlTemplateSerialiser.Serialise(doc);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Equal("Hello World", doc2.Nodes[0].Directives[0].Value!.String);
    }

    [Fact]
    public void EmptyPatch_Accepted()
    {
        const string kdl = """
            patch "EntityTemplate" "test" {}
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Single(doc.Nodes);
        Assert.Empty(doc.Nodes[0].Directives);
    }

    [Fact]
    public void CloneWithoutDirectives_RoundTrips()
    {
        const string kdl = """
            clone "UnitLeaderTemplate" from="base" id="derived"
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Single(doc.Nodes);
        Assert.Equal(KdlEditorNodeKind.Clone, doc.Nodes[0].Kind);
        Assert.Empty(doc.Nodes[0].Directives);

        var text = KdlTemplateSerialiser.Serialise(doc);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Equal(KdlEditorNodeKind.Clone, doc2.Nodes[0].Kind);
        Assert.Equal("base", doc2.Nodes[0].SourceId);
        Assert.Equal("derived", doc2.Nodes[0].CloneId);
    }

    [Fact]
    public void ParseText_RecoversSyntaxErrorsAcrossBlocks()
    {
        var input = """
            clone "UnitLeaderTemplate" from="squad_leader.darby" id="squad_leader.darby_jiangyu_clone" {
                set "InitialAttributes.Vitality" 77
            }a

            patch "UnitLeaderTemplate" "squad_leader.darby" {
                set "InitialAttributes.Agility" 100
                append "PerkTrees" ref="PerkTreeTemplate" "perk_tree.greifinger"
                insert "PerkTrees" index=0 ref="PerkTreeTemplate" "perk_tree.tech"
                remove "PerkTrees[0]"
            }adwad ggggg
            """;
        var doc = KdlTemplateParser.ParseText(input);
        Assert.Equal(2, doc.Errors.Count);
        Assert.Equal(3, doc.Errors[0].Line);  // }a
        Assert.Equal(10, doc.Errors[1].Line); // }adwad ggggg
    }

    [Fact]
    public void ParseText_RecoveryStillParsesValidBlocks()
    {
        // First block has a syntax error, second is valid
        var input = """
            clone "UnitLeaderTemplate" from="base" id="derived" {
                set "InitialAttributes.Vitality" 77
            }JUNK

            patch "UnitLeaderTemplate" "squad_leader.darby" {
                set "InitialAttributes.Agility" 100
            }
            """;
        var doc = KdlTemplateParser.ParseText(input);
        Assert.Single(doc.Errors);
        Assert.Equal(3, doc.Errors[0].Line);
        Assert.Single(doc.Nodes);
        Assert.Equal(KdlEditorNodeKind.Patch, doc.Nodes[0].Kind);
    }

    [Fact]
    public void ParseText_ValidInputBypassesRecovery()
    {
        // No syntax errors — should take the fast path, not recovery
        var input = """
            patch "EntityTemplate" "a" {
                set "X" 1
            }

            clone "UnitLeaderTemplate" from="b" id="c" {
                set "Y" 2
            }
            """;
        var doc = KdlTemplateParser.ParseText(input);
        Assert.Empty(doc.Errors);
        Assert.Equal(2, doc.Nodes.Count);
    }

    [Fact]
    public void ParseText_RecoveryWithSemanticAndSyntaxErrors()
    {
        // Block 1: valid KDL but unknown node (semantic error)
        // Block 2: syntax error
        var input = """
            foobar "something" {
                set "x" 1
            }

            patch "EntityTemplate" "test" {
                set "Y" 2
            }BROKEN
            """;
        var doc = KdlTemplateParser.ParseText(input);
        Assert.Equal(2, doc.Errors.Count);
        Assert.Contains(doc.Errors, e => e.Message.Contains("foobar"));
        Assert.Contains(doc.Errors, e => e.Line == 7);
    }

    [Fact]
    public void ParseText_RecoveryWithBracesInStrings()
    {
        // Braces inside strings should not confuse the block splitter
        var input = """
            patch "EntityTemplate" "test" {
                set "Name" "hello { world }"
            }OOPS
            """;
        var doc = KdlTemplateParser.ParseText(input);
        Assert.Single(doc.Errors);
        Assert.Equal(3, doc.Errors[0].Line);
    }

    [Fact]
    public void ParseText_RecoveryWithUnclosedBlock()
    {
        // Unclosed brace should still be captured as a block
        var input = """
            patch "EntityTemplate" "test" {
                set "X" 1
            """;
        var doc = KdlTemplateParser.ParseText(input);
        Assert.NotEmpty(doc.Errors);
    }

    [Fact]
    public void Serialise_FloatValue_UsesInvariantCulture()
    {
        // Switch the current culture to one that uses ',' as the decimal
        // separator. The serialiser must still emit '.' so KDL can re-parse it.
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var doc = new KdlEditorDocument
            {
                Nodes =
                [
                    new KdlEditorNode
                    {
                        Kind = KdlEditorNodeKind.Patch,
                        TemplateType = "EntityTemplate",
                        TemplateId = "x",
                        Directives =
                        [
                            new KdlEditorDirective
                            {
                                Op = KdlEditorOp.Set,
                                FieldPath = "Scale",
                                Value = new KdlEditorValue
                                {
                                    Kind = KdlEditorValueKind.Single,
                                    Single = 1.5f,
                                },
                            },
                        ],
                    },
                ],
            };

            var text = KdlTemplateSerialiser.Serialise(doc);
            Assert.Contains("1.5", text);
            Assert.DoesNotContain("1,5", text);

            // Round-trip through the parser to confirm KDL can read it back.
            var doc2 = KdlTemplateParser.ParseText(text);
            Assert.Empty(doc2.Errors);
            Assert.Equal(1.5f, doc2.Nodes[0].Directives[0].Value!.Single);
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Fact]
    public void Serialise_IntegerValue_UsesInvariantDigits()
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            // ar-SA traditionally renders Arabic-Indic digits when the
            // NumberFormat uses them; force a culture and verify the
            // serialiser still emits ASCII digits so KDL can parse them.
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");

            var doc = new KdlEditorDocument
            {
                Nodes =
                [
                    new KdlEditorNode
                    {
                        Kind = KdlEditorNodeKind.Patch,
                        TemplateType = "EntityTemplate",
                        TemplateId = "x",
                        Directives =
                        [
                            new KdlEditorDirective
                            {
                                Op = KdlEditorOp.Insert,
                                FieldPath = "Skills",
                                Index = 12,
                                Value = new KdlEditorValue
                                {
                                    Kind = KdlEditorValueKind.Int32,
                                    Int32 = 42,
                                },
                            },
                        ],
                    },
                ],
            };

            var text = KdlTemplateSerialiser.Serialise(doc);
            Assert.Contains("index=12", text);
            Assert.Contains("42", text);

            var doc2 = KdlTemplateParser.ParseText(text);
            Assert.Empty(doc2.Errors);
            Assert.Equal(12, doc2.Nodes[0].Directives[0].Index);
            Assert.Equal(42, doc2.Nodes[0].Directives[0].Value!.Int32);
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Fact]
    public void Serialise_StringValue_EscapesQuotesAndBackslashes()
    {
        var doc = new KdlEditorDocument
        {
            Nodes =
            [
                new KdlEditorNode
                {
                    Kind = KdlEditorNodeKind.Patch,
                    TemplateType = "EntityTemplate",
                    TemplateId = "x",
                    Directives =
                    [
                        new KdlEditorDirective
                        {
                            Op = KdlEditorOp.Set,
                            FieldPath = "Name",
                            Value = new KdlEditorValue
                            {
                                Kind = KdlEditorValueKind.String,
                                String = "He said \"hi\" \\path",
                            },
                        },
                    ],
                },
            ],
        };

        var text = KdlTemplateSerialiser.Serialise(doc);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Empty(doc2.Errors);
        Assert.Equal("He said \"hi\" \\path", doc2.Nodes[0].Directives[0].Value!.String);
    }

    [Fact]
    public void Serialise_EmptyDocument_ReturnsEmptyString()
    {
        var text = KdlTemplateSerialiser.Serialise(new KdlEditorDocument());
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void Serialise_PatchWithNoDirectives_EmitsEmptyBlock()
    {
        // Empty patches re-parse as errors (a patch must have at least one
        // operation). The serialiser still has to emit something stable so the
        // editor can show what it has, even if the result is invalid until the
        // user fills it in.
        var doc = new KdlEditorDocument
        {
            Nodes =
            [
                new KdlEditorNode
                {
                    Kind = KdlEditorNodeKind.Patch,
                    TemplateType = "EntityTemplate",
                    TemplateId = "x",
                    Directives = [],
                },
            ],
        };

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("patch \"EntityTemplate\" \"x\" {}", text);
    }

    [Fact]
    public void ZeroFloat_RoundTrips()
    {
        const string kdl = """
            patch "EntityTemplate" "x" {
                set "Scale" 0.0
            }
            """;
        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Equal(0f, doc.Nodes[0].Directives[0].Value!.Single);

        var text = KdlTemplateSerialiser.Serialise(doc);
        // Must still contain a decimal point so the parser sees a float, not an int.
        Assert.Contains("0.0", text);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Empty(doc2.Errors);
        Assert.Equal(KdlEditorValueKind.Single, doc2.Nodes[0].Directives[0].Value!.Kind);
    }

    [Fact]
    public void NestedComposite_IndentsConsistentlyWithDepth()
    {
        // Three composite levels deep: SoundBank.sounds (Sound) ->
        // Sound.variations (SoundVariation) -> SoundVariation fields.
        // Each inner block should sit one indent deeper than its parent,
        // and the closing brace should align with the line that opened it.
        const string kdl = """
            patch "SoundBank" "weapons_soundbank" {
                append "sounds" composite="Sound" from="aimed_shot" {
                    set "id" "test_rifle_fire"
                    clear "variations"
                    append "variations" composite="SoundVariation" {
                        set "clip" asset="weapons/test_rifle/fire_01"
                    }
                }
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);

        var text = KdlTemplateSerialiser.Serialise(doc);

        // The inner-most directive sits three levels deep (12 spaces) and
        // its closing brace lines up with the variations-block opener (8
        // spaces). The serialiser used to hardcode the composite child
        // block at indent=2, so this line was emitted at 8 spaces with a
        // closing brace at 4 instead.
        Assert.Contains("            set \"clip\" asset=\"weapons/test_rifle/fire_01\"\n", text);
        Assert.Contains("        }\n", text); // variations block close
        Assert.Contains("    }\n", text);     // sounds block close
        Assert.Contains("}\n", text);          // patch close

        // Round-trip: re-parsing the emitted text yields the same shape.
        var roundTripped = KdlTemplateParser.ParseText(text);
        Assert.Empty(roundTripped.Errors);
        var soundsAppend = roundTripped.Nodes[0].Directives[0];
        var soundComposite = soundsAppend.Value!.CompositeDirectives!;
        var variationsAppend = soundComposite.First(d => d.Op == KdlEditorOp.Append && d.FieldPath == "variations");
        Assert.NotNull(variationsAppend.Value!.CompositeDirectives);
        Assert.Single(variationsAppend.Value.CompositeDirectives);
    }

    [Fact]
    public void EmptyComposite_RoundTrips()
    {
        const string kdl = """
            patch "PerkTreeTemplate" "x" {
                append "Perks" composite="Perk" {
                    set "Tier" 1
                }
            }
            """;
        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);

        // Strip the composite's directives and re-serialise — the serialiser
        // should still emit a valid empty composite block.
        doc.Nodes[0].Directives[0].Value!.CompositeDirectives = null;
        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("composite=\"Perk\"", text);
    }

    // --- Descent block (child-block) round-trips ---

    [Fact]
    public void DescentBlock_RoundTrips_AsChildBlock()
    {
        // Authored as child block, parsed into flat directive(s) with hint,
        // serialised back to child block. Two-stage equivalence: text →
        // editor doc → text reproduces the descent shape.
        const string kdl = """
            patch "PerkTemplate" "perk.unique_darby_high_value_targets" {
                set "EventHandlers" index=0 type="AddSkill" {
                    set "ShowHUDText" #true
                }
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Single(doc.Nodes);
        Assert.Single(doc.Nodes[0].Directives);

        var d = doc.Nodes[0].Directives[0];
        Assert.Equal("ShowHUDText", d.FieldPath);
        Assert.NotNull(d.Descent);
        Assert.Single(d.Descent);
        Assert.Equal("EventHandlers", d.Descent[0].Field);
        Assert.Equal(0, d.Descent[0].Index);
        Assert.Equal("AddSkill", d.Descent[0].Subtype);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("set \"EventHandlers\" index=0 type=\"AddSkill\"", text);
        Assert.Contains("set \"ShowHUDText\" #true", text);
        Assert.DoesNotContain("[0]", text);

        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Empty(doc2.Errors);
        Assert.Single(doc2.Nodes[0].Directives);
        Assert.Equal(d.FieldPath, doc2.Nodes[0].Directives[0].FieldPath);
        Assert.NotNull(doc2.Nodes[0].Directives[0].Descent);
        Assert.Single(doc2.Nodes[0].Directives[0].Descent!);
        Assert.Equal("EventHandlers", doc2.Nodes[0].Directives[0].Descent![0].Field);
        Assert.Equal(0, doc2.Nodes[0].Directives[0].Descent![0].Index);
        Assert.Equal("AddSkill", doc2.Nodes[0].Directives[0].Descent![0].Subtype);
    }

    [Fact]
    public void ScalarPolymorphicDescent_RoundTrips_NoIndex()
    {
        // Phase 2a: descent into a non-collection polymorphic field. The
        // outer step has type= but no index=, marking scalar descent in the
        // wire format (TemplateDescentStep.Index is null). The serialiser
        // must omit index= so the round-trip parses back to the same
        // null-Index step rather than synthesising index=0.
        const string kdl = """
            patch "Attack" "fire_assault_rifle_attack" {
                set "DamageFilterCondition" type="MoraleStateCondition" {
                    set "MoraleState" 1
                }
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        var directive = Assert.Single(doc.Nodes[0].Directives);
        var step = Assert.Single(directive.Descent!);
        Assert.Equal("DamageFilterCondition", step.Field);
        Assert.Null(step.Index);
        Assert.Equal("MoraleStateCondition", step.Subtype);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("set \"DamageFilterCondition\" type=\"MoraleStateCondition\"", text);
        // Critical guard: the serialiser must NOT emit index= for a scalar
        // descent step. Otherwise the round-trip turns scalar descent into
        // a (non-existent) collection descent at element 0.
        Assert.DoesNotContain("DamageFilterCondition\" index=", text);

        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Empty(doc2.Errors);
        var step2 = Assert.Single(doc2.Nodes[0].Directives[0].Descent!);
        Assert.Equal("DamageFilterCondition", step2.Field);
        Assert.Null(step2.Index);
        Assert.Equal("MoraleStateCondition", step2.Subtype);
    }

    [Fact]
    public void DescentBlock_MultipleSiblingsGroupUnderOneOuter()
    {
        const string kdl = """
            patch "PerkTemplate" "perk.x" {
                set "EventHandlers" index=0 type="AddSkill" {
                    set "ShowHUDText" #true
                    set "OnlyApplyOnHit" #true
                }
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Equal(2, doc.Nodes[0].Directives.Count);

        var text = KdlTemplateSerialiser.Serialise(doc);

        // Single outer wrapper, two inner sets.
        var outerCount = System.Text.RegularExpressions.Regex.Matches(
            text, @"set ""EventHandlers"" index=0").Count;
        Assert.Equal(1, outerCount);
        Assert.Contains("set \"ShowHUDText\" #true", text);
        Assert.Contains("set \"OnlyApplyOnHit\" #true", text);
    }

    [Fact]
    public void DescentBlock_DifferentHints_EmitSeparateOuterBlocks()
    {
        // Two consecutive descent ops with the SAME outer (Field, index) but
        // DIFFERENT type= hints must not be folded together — that would
        // change the validated subtype for some inner ops. Each gets its
        // own outer wrapper.
        var doc = new KdlEditorDocument();
        doc.Nodes.Add(new KdlEditorNode
        {
            Kind = KdlEditorNodeKind.Patch,
            TemplateType = "PerkTemplate",
            TemplateId = "perk.x",
            Directives =
            [
                new KdlEditorDirective
                {
                    Op = KdlEditorOp.Set,
                    FieldPath = "FieldA",
                    Descent =
                    [
                        new TemplateDescentStep { Field = "EventHandlers", Index = 0, Subtype = "TypeA" },
                    ],
                    Value = new KdlEditorValue { Kind = KdlEditorValueKind.Boolean, Boolean = true },
                },
                new KdlEditorDirective
                {
                    Op = KdlEditorOp.Set,
                    FieldPath = "FieldB",
                    Descent =
                    [
                        new TemplateDescentStep { Field = "EventHandlers", Index = 0, Subtype = "TypeB" },
                    ],
                    Value = new KdlEditorValue { Kind = KdlEditorValueKind.Boolean, Boolean = false },
                },
            ],
        });

        var text = KdlTemplateSerialiser.Serialise(doc);

        var outerCount = System.Text.RegularExpressions.Regex.Matches(
            text, @"set ""EventHandlers"" index=0").Count;
        Assert.Equal(2, outerCount);
        Assert.Contains("type=\"TypeA\"", text);
        Assert.Contains("type=\"TypeB\"", text);
    }

    [Fact]
    public void DescentBlock_NestedDescent_RoundTrips()
    {
        const string kdl = """
            patch "PerkTemplate" "perk.x" {
                set "Outer" index=0 type="X" {
                    set "Inner" index=2 type="Y" {
                        set "Leaf" 5
                    }
                }
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Single(doc.Nodes[0].Directives);

        var d = doc.Nodes[0].Directives[0];
        Assert.Equal("Leaf", d.FieldPath);
        Assert.NotNull(d.Descent);
        Assert.Equal(2, d.Descent.Count);
        Assert.Equal("Outer", d.Descent[0].Field);
        Assert.Equal(0, d.Descent[0].Index);
        Assert.Equal("X", d.Descent[0].Subtype);
        Assert.Equal("Inner", d.Descent[1].Field);
        Assert.Equal(2, d.Descent[1].Index);
        Assert.Equal("Y", d.Descent[1].Subtype);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("set \"Outer\" index=0 type=\"X\"", text);
        Assert.Contains("set \"Inner\" index=2 type=\"Y\"", text);
        Assert.Contains("set \"Leaf\" 5", text);
        Assert.DoesNotContain("[", text);

        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Empty(doc2.Errors);
        Assert.Equal(d.FieldPath, doc2.Nodes[0].Directives[0].FieldPath);
        Assert.NotNull(doc2.Nodes[0].Directives[0].Descent);
        Assert.Equal(2, doc2.Nodes[0].Directives[0].Descent!.Count);
    }

    [Fact]
    public void DescentBlock_TerminalIndexer_EmitsAsIndexProperty()
    {
        // A directive that targets element N of a collection at the terminal
        // (no descent) is encoded as FieldPath="Field" + Index=N and must
        // serialise as set "Field" index=N value.
        var doc = new KdlEditorDocument();
        doc.Nodes.Add(new KdlEditorNode
        {
            Kind = KdlEditorNodeKind.Patch,
            TemplateType = "EntityTemplate",
            TemplateId = "player.x",
            Directives =
            [
                new KdlEditorDirective
                {
                    Op = KdlEditorOp.Set,
                    FieldPath = "InitialAttributes",
                    Index = 3,
                    Value = new KdlEditorValue { Kind = KdlEditorValueKind.Int32, Int32 = 7 },
                },
            ],
        });

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("set \"InitialAttributes\" index=3 7", text);
        Assert.DoesNotContain("[3]", text);

        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Empty(doc2.Errors);
        var d = doc2.Nodes[0].Directives[0];
        Assert.Equal("InitialAttributes", d.FieldPath);
        Assert.Equal(3, d.Index);
    }

    [Fact]
    public void DescentBlock_MixesWithFlatOps_PreservesOrder()
    {
        const string kdl = """
            patch "PerkTemplate" "perk.x" {
                set "Foo" 1
                set "EventHandlers" index=0 type="AddSkill" {
                    set "ShowHUDText" #true
                }
                set "Bar" 2
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Equal(3, doc.Nodes[0].Directives.Count);

        var text = KdlTemplateSerialiser.Serialise(doc);
        var lines = text.Split('\n');
        var fooLine = Array.FindIndex(lines, l => l.Contains("\"Foo\""));
        var ehLine = Array.FindIndex(lines, l => l.Contains("\"EventHandlers\""));
        var barLine = Array.FindIndex(lines, l => l.Contains("\"Bar\""));
        Assert.True(fooLine >= 0 && ehLine > fooLine && barLine > ehLine,
            $"order broken: foo={fooLine} eh={ehLine} bar={barLine}\n---\n{text}");
    }

    [Fact]
    public void HandlerConstruction_AppendRoundTrips()
    {
        const string kdl = """
            patch "SkillTemplate" "active.foo" {
                append "EventHandlers" handler="AddSkill" {
                    set "Event" enum="AddEvent" "OnHit"
                    set "OnlyApplyOnHit" #true
                }
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Single(doc.Nodes[0].Directives);

        var d = doc.Nodes[0].Directives[0];
        Assert.Equal(KdlEditorOp.Append, d.Op);
        Assert.Equal(KdlEditorValueKind.HandlerConstruction, d.Value!.Kind);
        Assert.Equal("AddSkill", d.Value.CompositeType);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("append \"EventHandlers\" handler=\"AddSkill\"", text);
        Assert.Contains("set \"Event\" enum=\"AddEvent\" \"OnHit\"", text);
        Assert.Contains("set \"OnlyApplyOnHit\" #true", text);
        Assert.DoesNotContain("composite=", text);

        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Empty(doc2.Errors);
        Assert.Equal(KdlEditorValueKind.HandlerConstruction, doc2.Nodes[0].Directives[0].Value!.Kind);
    }

    [Fact]
    public void HandlerConstruction_InsertCarriesIndexThroughRoundTrip()
    {
        const string kdl = """
            patch "SkillTemplate" "active.foo" {
                insert "EventHandlers" index=2 handler="ChangeProperty" {
                    set "Amount" 5
                }
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        var d = doc.Nodes[0].Directives[0];
        Assert.Equal(KdlEditorOp.Insert, d.Op);
        Assert.Equal(2, d.Index);
        Assert.Equal("ChangeProperty", d.Value!.CompositeType);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("insert \"EventHandlers\" index=2 handler=\"ChangeProperty\"", text);

        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Empty(doc2.Errors);
        Assert.Equal(2, doc2.Nodes[0].Directives[0].Index);
    }

    [Fact]
    public void DescentBlock_TypeHintOptional_EmitsWithoutType()
    {
        const string kdl = """
            patch "EntityTemplate" "player.x" {
                set "Properties" index=0 {
                    set "Accuracy" 80.0
                }
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        var d = doc.Nodes[0].Directives[0];
        Assert.NotNull(d.Descent);
        Assert.Single(d.Descent);
        Assert.Equal("Properties", d.Descent[0].Field);
        Assert.Equal(0, d.Descent[0].Index);
        Assert.Null(d.Descent[0].Subtype);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("set \"Properties\" index=0 {", text);
        Assert.DoesNotContain("type=", text);
    }
}
